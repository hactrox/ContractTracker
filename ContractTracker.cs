using Neo.IO.Json;
using Neo.Ledger;
using Neo.Persistence;
using System.Collections.Generic;
using System;
using Neo.IO;
using Neo.IO.Caching;
using Neo.IO.Data.LevelDB;
using static System.IO.Path;
using System.Linq;
using Neo.SmartContract;
using Neo.Network.P2P.Payloads;
using Neo.VM;
using Neo.VM.Types;
using System.Numerics;
using Microsoft.EntityFrameworkCore.Internal;
using static Neo.IO.Caching.DataCache<Neo.UInt160, Neo.Ledger.ContractState>;

namespace Neo.Plugins
{
    public class ContractTracker : Plugin, IPersistencePlugin
    {
        public override string Name => "ContractTracker";
        public override string Description => "Synchronizes the smart contract states";

        private readonly DB db;
        private int maxId;
        private SortedDictionary<long, List<JObject>> contractStates;

        public ContractTracker()
        {
            db = DB.Open(GetFullPath(Settings.Default.Path), new Options { CreateIfMissing = true });
            contractStates = new SortedDictionary<long, List<JObject>>();
            LoadContracts();
            RpcServerPlugin.RegisterMethods(this);
        }

        protected override void Configure()
        {
            Settings.Load(GetConfiguration());
        }

        [RpcMethod]
        public JObject GetContractStates(JArray _params)
        {
            if (_params.Count() < 2) return null;
            if (!int.TryParse(_params[0].AsString(), out int sinceBlockIndex)) return null;
            if (!int.TryParse(_params[1].AsString(), out int batches)) return null;

            JArray contracts = new JArray();

            foreach (var (_, stateArray) in contractStates.Where(s => s.Key >= sinceBlockIndex).Take(batches))
                stateArray.ForEach(obj => contracts.Add(obj));

            return contracts;
        }

        private void LoadContracts()
        {
            var nextPrefix = 0;
            while(true)
            {
                var rawData = db.Get(ReadOptions.Default, BitConverter.GetBytes(++nextPrefix));
                if (rawData == null) break;

                var json = JObject.Parse(rawData);
                var blockIndex = uint.Parse(json["blockindex"].ToString());
                AddContractState(blockIndex, json);

                Console.WriteLine($"Contract {json["hash"]} At {json["txid"]?.ToString()} state {json["state"]}");
                Console.Write($" block {blockIndex} time {ulong.Parse(json["blocktime"].ToString())} loaded from db.");
                Console.Write($" {json["name"]} {json["symbol"]} {json["decimals"]}");
            }

            maxId = nextPrefix - 1;
        }

        public void OnPersist(StoreView snapshot, IReadOnlyList<Blockchain.ApplicationExecuted> applicationExecutedList)
        {
            var block = snapshot.PersistingBlock;

            foreach(var trackable in snapshot.Contracts.GetChangeSet())
            {
                var state = trackable.State;
                var cs = trackable.Item;

                var json = cs.ToJson();
                json["blockindex"] = block.Index;
                json["blocktime"] = block.Timestamp;
                json["state"] = state;
                json["txid"] = FindTxID(block, trackable, applicationExecutedList);

                var (name, symbol, decimals, totalSupply) = GetNEP5Info(snapshot, cs);
                if (name != null && symbol != null && decimals != null)
                {
                    json["name"] = name;
                    json["symbol"] = symbol;
                    json["decimals"] = (int)decimals.Value;
                    json["totalsupply"] = totalSupply.Value.ToString();
                }

                AddContractState(block.Index, json);
                db.Put(WriteOptions.Default, BitConverter.GetBytes(++maxId), json.ToByteArray(false));
                Console.WriteLine($"Contract {cs.Manifest.Hash} At {json["txid"]} At Block {block.Index} {state}. {name} {symbol} {decimals}");
            }
        }

        private (string, string, BigInteger?, BigInteger?) GetNEP5Info(StoreView snapshot, ContractState cs)
        {
            (string, string, BigInteger?, BigInteger?) nil = (null, null, null, null);

            foreach (var std in cs.Manifest.SupportedStandards)
            {
                if (std.Replace("-", "").ToLower() == "nep5")
                {
                    var nameResult = Invoke(snapshot, cs.ScriptHash, "name");
                    if (nameResult == null || nameResult.Type != StackItemType.ByteString) return nil;

                    var symbolResult = Invoke(snapshot, cs.ScriptHash, "symbol");
                    if (symbolResult == null || symbolResult.Type != StackItemType.ByteString) return nil;

                    var decimalsResult = Invoke(snapshot, cs.ScriptHash, "decimals");
                    if (decimalsResult == null || decimalsResult.Type != StackItemType.Integer) return nil;

                    var totalSupplyResult = Invoke(snapshot, cs.ScriptHash, "totalSupply");
                    if (totalSupplyResult == null || totalSupplyResult.Type != StackItemType.Integer) return nil;

                    return (
                        nameResult.GetString(),
                        symbolResult.GetString(),
                        decimalsResult.GetInteger(),
                        totalSupplyResult.GetInteger()
                    );
                }
            }

            return nil;
        }

        private StackItem Invoke(StoreView snapshot, UInt160 scriptHash, string method)
        {
            byte[] script;
            using (ScriptBuilder sb = new ScriptBuilder())
            {
                sb.EmitAppCall(scriptHash, method);
                script = sb.ToArray();
            }

            using ApplicationEngine engine = ApplicationEngine.Run(script, snapshot, gas: 100000000);
            if (engine.State.HasFlag(VMState.FAULT)) return null;
            if (engine.ResultStack.Count <= 0) return null;
            return engine.ResultStack.Pop();
        }

        private void AddContractState(uint blockIndex, JObject csJson)
        {
            contractStates.TryGetValue(blockIndex, out List<JObject> list);

            if (list == null)
            {
                list = new List<JObject>();
                contractStates.Add(blockIndex, list);
            }

            list.Add(csJson);
        }

        private string FindTxID(Block block, Trackable trackable, IReadOnlyList<Blockchain.ApplicationExecuted> applicationExecutedList)
        {
            if (block.Index == 0)
                return block.Transactions[0].Hash.ToString();

            var state = trackable.State;
            var item = trackable.Item;

            foreach (var exec in applicationExecutedList)
            {
                var tx = exec.Transaction;

                if (exec.VMState.HasFlag(VMState.FAULT)) continue;
                if (tx == null) continue;
                if (!tx.Script.Any()) continue;

                var script = tx.Script.ToHexString();
                var txHash = tx.Hash.ToString();

                if (state == TrackState.Deleted)
                {
                    var call = new byte[] { 0x0C, 0x14 }.Concat(item.ScriptHash.ToArray()).ToArray().ToHexString();
                    if (script.Contains(call))
                    {
                        if (script.Contains("41c69f1df0") || script.Contains("4131c6331d")) // Destroy, Update
                            return txHash;
                        else if (script.Contains("41627d5b52")) // Call
                        {
                            if (contractStates.TryGetValue(maxId, out List<JObject> list) && list.Any()
                                && script.Contains(Convert.FromBase64String(list.Last()["script"].AsString()).ToHexString()))
                                    return txHash;
                        }
                        else
                            return "";
                    }
                }

                if (script.Contains(item.Script.ToHexString()))
                    return txHash;
            }

            return "";
        }

        public void OnCommit(StoreView snapshot)
        {
        }

        public bool ShouldThrowExceptionFromCommit(Exception ex)
        {
            return false;
        }
    }
}
