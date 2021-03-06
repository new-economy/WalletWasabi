﻿using WalletWasabi.Backend;
using WalletWasabi.Backend.Models;
using WalletWasabi.Exceptions;
using WalletWasabi.KeyManagement;
using WalletWasabi.Logging;
using WalletWasabi.Models;
using WalletWasabi.Services;
using WalletWasabi.Tests.NodeBuilding;
using WalletWasabi.TorSocks5;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using NBitcoin;
using NBitcoin.Protocol;
using NBitcoin.Protocol.Behaviors;
using NBitcoin.RPC;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Asn1.IsisMtt.X509;
using Xunit;
using WalletWasabi.Backend.Models.Responses;
using WalletWasabi.ChaumianCoinJoin;
using WalletWasabi.Backend.Models.Requests;
using WalletWasabi.Crypto;
using WalletWasabi.Helpers;
using Org.BouncyCastle.Math;
using WalletWasabi.WebClients.ChaumianCoinJoin;
using System.Security;

namespace WalletWasabi.Tests
{
	[Collection("RegTest collection")]
	public class RegTests : IClassFixture<SharedFixture>
	{
		public const uint ProtocolVersion_WITNESS_VERSION = 70012;
		private SharedFixture SharedFixture { get; }

		private RegTestFixture RegTestFixture { get; }

		public RegTests(SharedFixture sharedFixture, RegTestFixture regTestFixture)
		{
			SharedFixture = sharedFixture;
			RegTestFixture = regTestFixture;
		}

		private async Task AssertFiltersInitializedAsync()
		{
			var firstHash = await Global.RpcClient.GetBlockHashAsync(0);
			while (true)
			{
				using (var client = new TorHttpClient(new Uri(RegTestFixture.BackendEndPoint)))
				using (var response = await client.SendAsync(HttpMethod.Get, $"/api/v1/btc/blockchain/filters?bestKnownBlockHash={firstHash}&count=1000"))
				{
					Assert.Equal(HttpStatusCode.OK, response.StatusCode);
					var filters = await response.Content.ReadAsJsonAsync<List<string>>();
					var filterCount = filters.Count();
					if (filterCount >= 101)
					{
						break;
					}
					else
					{
						await Task.Delay(100);
					}
				}
			}
		}

		#region BackendTests

		[Fact]
		public async void GetExchangeRatesAsyncAsync()
		{
			using (var client = new TorHttpClient(new Uri(RegTestFixture.BackendEndPoint)))
			using (var response = await client.SendAsync(HttpMethod.Get, "/api/v1/btc/offchain/exchange-rates"))
			{
				Assert.True(response.StatusCode == HttpStatusCode.OK);

				var exchangeRates = await response.Content.ReadAsJsonAsync<List<ExchangeRate>>();
				Assert.Single(exchangeRates);

				var rate = exchangeRates[0];
				Assert.Equal("USD", rate.Ticker);
				Assert.True(rate.Rate > 0);
			}
		}

		[Fact]
		public async void BroadcastWithOutMinFeeAsync()
		{
			await Global.RpcClient.GenerateAsync(1);
			var utxos = await Global.RpcClient.ListUnspentAsync();
			var utxo = utxos[0];
			var addr = await Global.RpcClient.GetNewAddressAsync();
			var tx = new Transaction();
			tx.Inputs.Add(new TxIn(utxo.OutPoint, Script.Empty));
			tx.Outputs.Add(new TxOut(utxo.Amount, addr));
			var signedTx = await Global.RpcClient.SignRawTransactionAsync(tx);

			var content = new StringContent($"'{signedTx.ToHex()}'", Encoding.UTF8, "application/json");

			Logger.TurnOff();
			using (var client = new TorHttpClient(new Uri(RegTestFixture.BackendEndPoint)))
			using (var response = await client.SendAsync(HttpMethod.Post, "/api/v1/btc/blockchain/broadcast", content))
			{
				Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
				Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
			}
			Logger.TurnOn();
		}

		[Fact]
		public async void BroadcastReplayTxAsync()
		{
			await Global.RpcClient.GenerateAsync(1);
			var utxos = await Global.RpcClient.ListUnspentAsync();
			var utxo = utxos[0];
			var tx = await Global.RpcClient.GetRawTransactionAsync(utxo.OutPoint.Hash);
			var content = new StringContent($"'{tx.ToHex()}'", Encoding.UTF8, "application/json");

			Logger.TurnOff();
			using (var client = new TorHttpClient(new Uri(RegTestFixture.BackendEndPoint)))
			using (var response = await client.SendAsync(HttpMethod.Post, "/api/v1/btc/blockchain/broadcast", content))
			{
				Assert.Equal(HttpStatusCode.OK, response.StatusCode);
				Assert.Equal("Transaction is already in the blockchain.", await response.Content.ReadAsJsonAsync<string>());
			}
			Logger.TurnOn();
		}

		[Fact]
		public async void BroadcastInvalidTxAsync()
		{
			var content = new StringContent($"''", Encoding.UTF8, "application/json");

			Logger.TurnOff();
			using (var client = new TorHttpClient(new Uri(RegTestFixture.BackendEndPoint)))
			using (var response = await client.SendAsync(HttpMethod.Post, "/api/v1/btc/blockchain/broadcast", content))
			{
				Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
				Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
				Assert.Equal("Invalid hex.", await response.Content.ReadAsJsonAsync<string>());
			}
			Logger.TurnOn();
		}

		#endregion

		#region ServicesTests

		[Fact]
		public async Task MempoolAsync()
		{
			var memPoolService = new MemPoolService();
			Node node = RegTestFixture.BackendRegTestNode.CreateNodeClient();
			node.Behaviors.Add(new MemPoolBehavior(memPoolService));
			node.VersionHandshake();

			try
			{
				memPoolService.TransactionReceived += MempoolAsync_MemPoolService_TransactionReceived;

				// Using the interlocked, not because it makes sense in this context, but to
				// set an example that these values are often concurrency sensitive
				for (int i = 0; i < 10; i++)
				{
					var addr = await Global.RpcClient.GetNewAddressAsync();
					var res = await Global.RpcClient.SendToAddressAsync(addr, new Money(0.01m, MoneyUnit.BTC));
					Assert.NotNull(res);
				}

				var times = 0;
				while (Interlocked.Read(ref _mempoolTransactionCount) < 10)
				{
					if (times > 100) // 10 seconds
					{
						throw new TimeoutException($"{nameof(MemPoolService)} test timed out.");
					}
					await Task.Delay(100);
					times++;
				}
			}
			finally
			{
				memPoolService.TransactionReceived -= MempoolAsync_MemPoolService_TransactionReceived;
			}
		}

		private long _mempoolTransactionCount = 0;
		private void MempoolAsync_MemPoolService_TransactionReceived(object sender, SmartTransaction e)
		{
			Interlocked.Increment(ref _mempoolTransactionCount);
			Logger.LogDebug<P2pTests>($"Mempool transaction received: {e.GetHash()}.");
		}

		[Fact]
		public async Task FilterDownloaderTestAsync()
		{
			await AssertFiltersInitializedAsync();

			var indexFilePath = Path.Combine(SharedFixture.DataDir, nameof(FilterDownloaderTestAsync), $"Index{Global.RpcClient.Network}.dat");

			var downloader = new IndexDownloader(Global.RpcClient.Network, indexFilePath, new Uri(RegTestFixture.BackendEndPoint));
			try
			{
				downloader.Synchronize(requestInterval: TimeSpan.FromSeconds(1));

				// Test initial synchronization.

				var times = 0;
				int filterCount;
				while ((filterCount = downloader.GetFiltersIncluding(Network.RegTest.GenesisHash).Count()) < 102)
				{
					if (times > 500) // 30 sec
					{
						throw new TimeoutException($"{nameof(IndexDownloader)} test timed out. Needed filters: {102}, got only: {filterCount}.");
					}
					await Task.Delay(100);
					times++;
				}

				// Test later synchronization.
				RegTestFixture.BackendRegTestNode.Generate(10);
				times = 0;
				while ((filterCount = downloader.GetFiltersIncluding(new Height(0)).Count()) < 112)
				{
					if (times > 500) // 30 sec
					{
						throw new TimeoutException($"{nameof(IndexDownloader)} test timed out. Needed filters: {112}, got only: {filterCount}.");
					}
					await Task.Delay(100);
					times++;
				}

				// Test correct number of filters is received.
				var hundredthHash = await Global.RpcClient.GetBlockHashAsync(100);
				Assert.Equal(100, downloader.GetFiltersIncluding(hundredthHash).First().BlockHeight.Value);

				// Test filter block hashes are correct.
				var filters = downloader.GetFiltersIncluding(Network.RegTest.GenesisHash).ToArray();
				for (int i = 0; i < 101; i++)
				{
					var expectedHash = await Global.RpcClient.GetBlockHashAsync(i);
					var filter = filters[i];
					Assert.Equal(i, filter.BlockHeight.Value);
					Assert.Equal(expectedHash, filter.BlockHash);
					Assert.Null(filter.Filter);
				}
			}
			finally
			{
				if (downloader != null)
				{
					await downloader.StopAsync();
				}
			}
		}

		[Fact]
		public async Task ReorgTestAsync()
		{
			await AssertFiltersInitializedAsync();

			var network = Network.RegTest;
			var keyManager = KeyManager.CreateNew(out _, "password");

			// Mine some coins, make a few bech32 transactions then make it confirm.
			await Global.RpcClient.GenerateAsync(1);
			var key = keyManager.GenerateNewKey("", KeyState.Clean, isInternal: false);
			var tx2 = await Global.RpcClient.SendToAddressAsync(key.GetP2wpkhAddress(network), new Money(0.1m, MoneyUnit.BTC));
			key = keyManager.GenerateNewKey("", KeyState.Clean, isInternal: false);
			var tx3 = await Global.RpcClient.SendToAddressAsync(key.GetP2wpkhAddress(network), new Money(0.1m, MoneyUnit.BTC));
			var tx4 = await Global.RpcClient.SendToAddressAsync(key.GetP2pkhAddress(network), new Money(0.1m, MoneyUnit.BTC));
			var tx5 = await Global.RpcClient.SendToAddressAsync(key.GetP2shOverP2wpkhAddress(network), new Money(0.1m, MoneyUnit.BTC));
			var tx1 = await Global.RpcClient.SendToAddressAsync(key.GetP2wpkhAddress(network), new Money(0.1m, MoneyUnit.BTC), replaceable: true);

			await Global.RpcClient.GenerateAsync(2); // Generate two, so we can test for two reorg

			_reorgTestAsync_ReorgCount = 0;

			var node = RegTestFixture.BackendRegTestNode;
			var indexFilePath = Path.Combine(SharedFixture.DataDir, nameof(ReorgTestAsync), $"Index{Global.RpcClient.Network}.dat");

			var downloader = new IndexDownloader(Global.RpcClient.Network, indexFilePath, new Uri(RegTestFixture.BackendEndPoint));
			try
			{
				downloader.Synchronize(requestInterval: TimeSpan.FromSeconds(3));

				downloader.Reorged += ReorgTestAsync_Downloader_Reorged;

				// Test initial synchronization.	
				await WaitForIndexesToSyncAsync(TimeSpan.FromSeconds(90), downloader);

				var indexLines = await File.ReadAllLinesAsync(indexFilePath);
				var lastFilter = indexLines.Last();
				var tip = await Global.RpcClient.GetBestBlockHashAsync();
				Assert.StartsWith(tip.ToString(), indexLines.Last());
				var tipBlock = await Global.RpcClient.GetBlockHeaderAsync(tip);
				Assert.Contains(tipBlock.HashPrevBlock.ToString(), indexLines.TakeLast(2).First());

				var utxoPath = Global.IndexBuilderService.Bech32UtxoSetFilePath;
				var utxoLines = await File.ReadAllTextAsync(utxoPath);
				Assert.Contains(tx1.ToString(), utxoLines);
				Assert.Contains(tx2.ToString(), utxoLines);
				Assert.Contains(tx3.ToString(), utxoLines);
				Assert.DoesNotContain(tx4.ToString(), utxoLines); // make sure only bech is recorded
				Assert.DoesNotContain(tx5.ToString(), utxoLines); // make sure only bech is recorded

				// Test synchronization after fork.
				await Global.RpcClient.InvalidateBlockAsync(tip); // Reorg 1
				tip = await Global.RpcClient.GetBestBlockHashAsync();
				await Global.RpcClient.InvalidateBlockAsync(tip); // Reorg 2
				var tx1bumpRes = await Global.RpcClient.BumpFeeAsync(tx1); // RBF it

				await Global.RpcClient.GenerateAsync(5);
				await WaitForIndexesToSyncAsync(TimeSpan.FromSeconds(90), downloader);

				utxoLines = await File.ReadAllTextAsync(utxoPath);
				Assert.Contains(tx1bumpRes.TransactionId.ToString(), utxoLines); // assert the tx1bump is the correct tx
				Assert.DoesNotContain(tx1.ToString(), utxoLines); // assert tx1 is abandoned (despite it confirmed previously)
				Assert.Contains(tx2.ToString(), utxoLines);
				Assert.Contains(tx3.ToString(), utxoLines);
				Assert.DoesNotContain(tx4.ToString(), utxoLines);
				Assert.DoesNotContain(tx5.ToString(), utxoLines);

				indexLines = await File.ReadAllLinesAsync(indexFilePath);
				Assert.DoesNotContain(tip.ToString(), indexLines);
				Assert.DoesNotContain(tipBlock.HashPrevBlock.ToString(), indexLines);

				// Test filter block hashes are correct after fork.
				var filters = downloader.GetFiltersIncluding(Network.RegTest.GenesisHash).ToArray();
				var blockCountIncludingGenesis = await Global.RpcClient.GetBlockCountAsync() + 1;
				for (int i = 0; i < blockCountIncludingGenesis; i++)
				{
					var expectedHash = await Global.RpcClient.GetBlockHashAsync(i);
					var filter = filters[i];
					Assert.Equal(i, filter.BlockHeight.Value);
					Assert.Equal(expectedHash, filter.BlockHash);
					if (i < 101) // Later other tests may fill the filter.
					{
						Assert.Null(filter.Filter);
					}
				}

				// Test the serialization, too.
				tip = await Global.RpcClient.GetBestBlockHashAsync();
				var blockHash = tip;
				for (var i = 0; i < indexLines.Length; i++)
				{
					var block = await Global.RpcClient.GetBlockHeaderAsync(blockHash);
					Assert.Contains(blockHash.ToString(), indexLines[indexLines.Length - i - 1]);
					blockHash = block.HashPrevBlock;
				}

				// Assert reorg happened exactly as many times as we reorged.
				Assert.Equal(2, Interlocked.Read(ref _reorgTestAsync_ReorgCount));
			}
			finally
			{
				downloader.Reorged -= ReorgTestAsync_Downloader_Reorged;

				if (downloader != null)
				{
					await downloader.StopAsync();
				}
			}
		}

		private async Task WaitForIndexesToSyncAsync(TimeSpan timeout, IndexDownloader downloader)
		{
			var bestHash = await Global.RpcClient.GetBestBlockHashAsync();

			var times = 0;
			while (downloader.GetFiltersIncluding(new Height(0)).SingleOrDefault(x => x.BlockHash == bestHash) == null)
			{
				if (times > timeout.TotalSeconds)
				{
					throw new TimeoutException($"{nameof(IndexDownloader)} test timed out. Filter wasn't downloaded.");
				}
				await Task.Delay(TimeSpan.FromSeconds(1));
				times++;
			}
		}

		private long _reorgTestAsync_ReorgCount;
		private void ReorgTestAsync_Downloader_Reorged(object sender, uint256 e)
		{
			Assert.NotNull(e);
			Interlocked.Increment(ref _reorgTestAsync_ReorgCount);
		}

		#endregion

		#region ClientTests

		[Fact]
		public async Task WalletTestsAsync()
		{
			// Make sure fitlers are created on the server side.
			await AssertFiltersInitializedAsync();

			var network = Global.RpcClient.Network;

			// Create the services.
			// 1. Create connection service.
			var nodes = new NodesGroup(Global.Config.Network,
					requirements: new NodeRequirement
					{
						RequiredServices = NodeServices.Network,
						MinVersion = ProtocolVersion_WITNESS_VERSION
					});
			nodes.ConnectedNodes.Add(RegTestFixture.BackendRegTestNode.CreateNodeClient());

			// 2. Create mempool service.
			var memPoolService = new MemPoolService();
			memPoolService.TransactionReceived += WalletTestsAsync_MemPoolService_TransactionReceived;
			Node node = RegTestFixture.BackendRegTestNode.CreateNodeClient();
			node.Behaviors.Add(new MemPoolBehavior(memPoolService));

			// 3. Create index downloader service.
			var indexFilePath = Path.Combine(SharedFixture.DataDir, nameof(WalletTestsAsync), $"Index{Global.RpcClient.Network}.dat");
			var indexDownloader = new IndexDownloader(Global.RpcClient.Network, indexFilePath, new Uri(RegTestFixture.BackendEndPoint));

			// 4. Create key manager service.
			var keyManager = KeyManager.CreateNew(out _, "password");

			// 5. Create chaumian coinjoin client.
			var chaumianClient = new CcjClient(Global.RpcClient.Network, Global.Coordinator.RsaKey.PubKey, keyManager, new Uri(RegTestFixture.BackendEndPoint));

			// 5. Create wallet service.
			var blocksFolderPath = Path.Combine(SharedFixture.DataDir, nameof(WalletTestsAsync), $"Blocks");
			var wallet = new WalletService(keyManager, indexDownloader, chaumianClient, memPoolService, nodes, blocksFolderPath);
			wallet.NewFilterProcessed += Wallet_NewFilterProcessed;

			// Get some money, make it confirm.
			var key = wallet.GetReceiveKey("foo label");
			var txid = await Global.RpcClient.SendToAddressAsync(key.GetP2wpkhAddress(network), new Money(0.1m, MoneyUnit.BTC));
			await Global.RpcClient.GenerateAsync(1);

			try
			{
				_filtersProcessedByWalletCount = 0;
				nodes.Connect(); // Start connection service.
				node.VersionHandshake(); // Start mempool service.
				indexDownloader.Synchronize(requestInterval: TimeSpan.FromSeconds(3)); // Start index downloader service.
				chaumianClient.Start(); // Start chaumian coinjoin client.

				// Wait until the filter our previous transaction is present.
				var blockCount = await Global.RpcClient.GetBlockCountAsync();
				await WaitForFiltersToBeProcessedAsync(TimeSpan.FromSeconds(120), blockCount);

				using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
				{
					await wallet.InitializeAsync(cts.Token); // Initialize wallet service.
				}
				Assert.Equal(1, await wallet.CountBlocksAsync());

				Assert.Single(wallet.Coins);
				var firstCoin = wallet.Coins.Single();
				Assert.Equal(new Money(0.1m, MoneyUnit.BTC), firstCoin.Amount);
				Assert.Equal(indexDownloader.GetBestFilter().BlockHeight, firstCoin.Height);
				Assert.InRange(firstCoin.Index, 0, 1);
				Assert.False(firstCoin.SpentOrLocked);
				Assert.Equal("foo label", firstCoin.Label);
				Assert.Equal(key.GetP2wpkhScript(), firstCoin.ScriptPubKey);
				Assert.Null(firstCoin.SpenderTransactionId);
				Assert.NotNull(firstCoin.SpentOutputs);
				Assert.NotEmpty(firstCoin.SpentOutputs);
				Assert.Equal(txid, firstCoin.TransactionId);
				Assert.Single(keyManager.GetKeys(KeyState.Used, false));
				Assert.Equal("foo label", keyManager.GetKeys(KeyState.Used, false).Single().Label);

				// Get some money, make it confirm.
				var key2 = wallet.GetReceiveKey("bar label");
				var txid2 = await Global.RpcClient.SendToAddressAsync(key2.GetP2wpkhAddress(network), new Money(0.01m, MoneyUnit.BTC));
				_filtersProcessedByWalletCount = 0;
				await Global.RpcClient.GenerateAsync(1);
				var txid3 = await Global.RpcClient.SendToAddressAsync(key2.GetP2wpkhAddress(network), new Money(0.02m, MoneyUnit.BTC));
				await Global.RpcClient.GenerateAsync(1);

				await WaitForFiltersToBeProcessedAsync(TimeSpan.FromSeconds(120), 2);
				Assert.Equal(3, await wallet.CountBlocksAsync());

				Assert.Equal(3, wallet.Coins.Count);
				firstCoin = wallet.Coins.OrderBy(x => x.Height).First();
				var secondCoin = wallet.Coins.OrderBy(x => x.Height).Take(2).Last();
				var thirdCoin = wallet.Coins.OrderBy(x => x.Height).Last();
				Assert.Equal(new Money(0.01m, MoneyUnit.BTC), secondCoin.Amount);
				Assert.Equal(new Money(0.02m, MoneyUnit.BTC), thirdCoin.Amount);
				Assert.Equal(indexDownloader.GetBestFilter().BlockHeight.Value - 2, firstCoin.Height.Value);
				Assert.Equal(indexDownloader.GetBestFilter().BlockHeight.Value - 1, secondCoin.Height.Value);
				Assert.Equal(indexDownloader.GetBestFilter().BlockHeight, thirdCoin.Height);
				Assert.False(thirdCoin.SpentOrLocked);
				Assert.Equal("foo label", firstCoin.Label);
				Assert.Equal("bar label", secondCoin.Label);
				Assert.Equal("bar label", thirdCoin.Label);
				Assert.Equal(key.GetP2wpkhScript(), firstCoin.ScriptPubKey);
				Assert.Equal(key2.GetP2wpkhScript(), secondCoin.ScriptPubKey);
				Assert.Equal(key2.GetP2wpkhScript(), thirdCoin.ScriptPubKey);
				Assert.Null(thirdCoin.SpenderTransactionId);
				Assert.NotNull(firstCoin.SpentOutputs);
				Assert.NotNull(secondCoin.SpentOutputs);
				Assert.NotNull(thirdCoin.SpentOutputs);
				Assert.NotEmpty(firstCoin.SpentOutputs);
				Assert.NotEmpty(secondCoin.SpentOutputs);
				Assert.NotEmpty(thirdCoin.SpentOutputs);
				Assert.Equal(txid, firstCoin.TransactionId);
				Assert.Equal(txid2, secondCoin.TransactionId);
				Assert.Equal(txid3, thirdCoin.TransactionId);

				Assert.Equal(2, keyManager.GetKeys(KeyState.Used, false).Count());
				Assert.Empty(keyManager.GetKeys(KeyState.Used, true));
				Assert.Equal(2, keyManager.GetKeys(KeyState.Used).Count());
				Assert.Empty(keyManager.GetKeys(KeyState.Locked, false));
				Assert.Empty(keyManager.GetKeys(KeyState.Locked, true));
				Assert.Empty(keyManager.GetKeys(KeyState.Locked));
				Assert.Equal(21, keyManager.GetKeys(KeyState.Clean, true).Count());
				Assert.Equal(21, keyManager.GetKeys(KeyState.Clean, false).Count());
				Assert.Equal(42, keyManager.GetKeys(KeyState.Clean).Count());
				Assert.Equal(44, keyManager.GetKeys().Count());

				Assert.Single(keyManager.GetKeys(KeyState.Used, false).Where(x => x.Label == "foo label"));
				Assert.Single(keyManager.GetKeys(KeyState.Used, false).Where(x => x.Label == "bar label"));

				// REORG TESTS
				var txid4 = await Global.RpcClient.SendToAddressAsync(key2.GetP2wpkhAddress(network), new Money(0.03m, MoneyUnit.BTC), replaceable: true);
				_filtersProcessedByWalletCount = 0;
				await Global.RpcClient.GenerateAsync(2);
				await WaitForFiltersToBeProcessedAsync(TimeSpan.FromSeconds(120), 2);
				Assert.NotEmpty(wallet.Coins.Where(x => x.TransactionId == txid4));
				var tip = await Global.RpcClient.GetBestBlockHashAsync();
				await Global.RpcClient.InvalidateBlockAsync(tip); // Reorg 1
				tip = await Global.RpcClient.GetBestBlockHashAsync();
				await Global.RpcClient.InvalidateBlockAsync(tip); // Reorg 2
				var tx4bumpRes = await Global.RpcClient.BumpFeeAsync(txid4); // RBF it
				_filtersProcessedByWalletCount = 0;
				await Global.RpcClient.GenerateAsync(3);
				await WaitForFiltersToBeProcessedAsync(TimeSpan.FromSeconds(120), 3);

				Assert.Equal(4, await wallet.CountBlocksAsync());

				Assert.Equal(4, wallet.Coins.Count);
				Assert.Empty(wallet.Coins.Where(x => x.TransactionId == txid4));
				Assert.NotEmpty(wallet.Coins.Where(x => x.TransactionId == tx4bumpRes.TransactionId));
				var rbfCoin = wallet.Coins.Where(x => x.TransactionId == tx4bumpRes.TransactionId).Single();

				Assert.Equal(new Money(0.03m, MoneyUnit.BTC), rbfCoin.Amount);
				Assert.Equal(indexDownloader.GetBestFilter().BlockHeight.Value - 2, rbfCoin.Height.Value);
				Assert.False(rbfCoin.SpentOrLocked);
				Assert.Equal("bar label", rbfCoin.Label);
				Assert.Equal(key2.GetP2wpkhScript(), rbfCoin.ScriptPubKey);
				Assert.Null(rbfCoin.SpenderTransactionId);
				Assert.NotNull(rbfCoin.SpentOutputs);
				Assert.NotEmpty(rbfCoin.SpentOutputs);
				Assert.Equal(tx4bumpRes.TransactionId, rbfCoin.TransactionId);

				Assert.Equal(2, keyManager.GetKeys(KeyState.Used, false).Count());
				Assert.Empty(keyManager.GetKeys(KeyState.Used, true));
				Assert.Equal(2, keyManager.GetKeys(KeyState.Used).Count());
				Assert.Empty(keyManager.GetKeys(KeyState.Locked, false));
				Assert.Empty(keyManager.GetKeys(KeyState.Locked, true));
				Assert.Empty(keyManager.GetKeys(KeyState.Locked));
				Assert.Equal(21, keyManager.GetKeys(KeyState.Clean, true).Count());
				Assert.Equal(21, keyManager.GetKeys(KeyState.Clean, false).Count());
				Assert.Equal(42, keyManager.GetKeys(KeyState.Clean).Count());
				Assert.Equal(44, keyManager.GetKeys().Count());

				Assert.Single(keyManager.GetKeys(KeyState.Used, false).Where(x => x.Label == "foo label"));
				Assert.Single(keyManager.GetKeys(KeyState.Used, false).Where(x => x.Label == "bar label"));

				// TEST MEMPOOL
				var txid5 = await Global.RpcClient.SendToAddressAsync(key.GetP2wpkhAddress(network), new Money(0.1m, MoneyUnit.BTC));
				await Task.Delay(1000); // Wait tx to arrive and get processed.
				Assert.NotEmpty(wallet.Coins.Where(x => x.TransactionId == txid5));
				var mempoolCoin = wallet.Coins.Where(x => x.TransactionId == txid5).Single();
				Assert.Equal(Height.MemPool, mempoolCoin.Height);

				_filtersProcessedByWalletCount = 0;
				await Global.RpcClient.GenerateAsync(1);
				await WaitForFiltersToBeProcessedAsync(TimeSpan.FromSeconds(120), 1);
				var res = await Global.RpcClient.GetTxOutAsync(mempoolCoin.TransactionId, mempoolCoin.Index, true);
				Assert.Equal(indexDownloader.GetBestFilter().BlockHeight, mempoolCoin.Height);
			}
			finally
			{
				wallet.NewFilterProcessed -= Wallet_NewFilterProcessed;
				wallet?.Dispose();
				// Dispose index downloader service.
				if (indexDownloader != null)
				{
					await indexDownloader.StopAsync();
				}

				// Dispose mempool service.
				memPoolService.TransactionReceived -= WalletTestsAsync_MemPoolService_TransactionReceived;

				// Dispose connection service.
				nodes?.Dispose();

				// Dispose chaumian coinjoin client.
				if (chaumianClient != null)
				{
					await chaumianClient.StopAsync();
				}
			}
		}

		private async Task WaitForFiltersToBeProcessedAsync(TimeSpan timeout, int numberOfFiltersToWaitFor)
		{
			var times = 0;
			while (Interlocked.Read(ref _filtersProcessedByWalletCount) < numberOfFiltersToWaitFor)
			{
				if (times > timeout.TotalSeconds)
				{
					throw new TimeoutException($"{nameof(WalletService)} test timed out. Filter wasn't processed. Needed: {numberOfFiltersToWaitFor}, got only: {_filtersProcessedByWalletCount}.");
				}
				await Task.Delay(TimeSpan.FromSeconds(1));
				times++;
			}
		}

		private long _filtersProcessedByWalletCount;
		private void Wallet_NewFilterProcessed(object sender, FilterModel e)
		{
			Interlocked.Increment(ref _filtersProcessedByWalletCount);
		}

		private void WalletTestsAsync_MemPoolService_TransactionReceived(object sender, SmartTransaction e)
		{

		}

		[Fact]
		public async Task SendTestsFromHiddenWalletAsync() // These tests are taken from HiddenWallet, they were tests on the testnet.
		{
			// Make sure fitlers are created on the server side.
			await AssertFiltersInitializedAsync();

			var network = Global.RpcClient.Network;

			// Create the services.
			// 1. Create connection service.
			var nodes = new NodesGroup(Global.Config.Network,
					requirements: new NodeRequirement
					{
						RequiredServices = NodeServices.Network,
						MinVersion = ProtocolVersion_WITNESS_VERSION
					});
			nodes.ConnectedNodes.Add(RegTestFixture.BackendRegTestNode.CreateNodeClient());

			// 2. Create mempool service.
			var memPoolService = new MemPoolService();
			Node node = RegTestFixture.BackendRegTestNode.CreateNodeClient();
			node.Behaviors.Add(new MemPoolBehavior(memPoolService));

			// 3. Create index downloader service.
			var indexFilePath = Path.Combine(SharedFixture.DataDir, nameof(SendTestsFromHiddenWalletAsync), $"Index{Global.RpcClient.Network}.dat");
			var indexDownloader = new IndexDownloader(Global.RpcClient.Network, indexFilePath, new Uri(RegTestFixture.BackendEndPoint));

			// 4. Create key manager service.
			var keyManager = KeyManager.CreateNew(out _, "password");

			// 5. Create chaumian coinjoin client.
			var chaumianClient = new CcjClient(Global.RpcClient.Network, Global.Coordinator.RsaKey.PubKey, keyManager, new Uri(RegTestFixture.BackendEndPoint));

			// 6. Create wallet service.
			var blocksFolderPath = Path.Combine(SharedFixture.DataDir, nameof(SendTestsFromHiddenWalletAsync), $"Blocks");
			var wallet = new WalletService(keyManager, indexDownloader, chaumianClient, memPoolService, nodes, blocksFolderPath);
			wallet.NewFilterProcessed += Wallet_NewFilterProcessed;

			// Get some money, make it confirm.
			var key = wallet.GetReceiveKey("foo label");
			var txid = await Global.RpcClient.SendToAddressAsync(key.GetP2wpkhAddress(network), new Money(1m, MoneyUnit.BTC));
			await Global.RpcClient.GenerateAsync(2);

			try
			{
				_filtersProcessedByWalletCount = 0;
				nodes.Connect(); // Start connection service.
				node.VersionHandshake(); // Start mempool service.
				indexDownloader.Synchronize(requestInterval: TimeSpan.FromSeconds(3)); // Start index downloader service.
				chaumianClient.Start(); // Start chaumian coinjoin client.

				// Wait until the filter our previous transaction is present.
				var blockCount = await Global.RpcClient.GetBlockCountAsync();
				await WaitForFiltersToBeProcessedAsync(TimeSpan.FromSeconds(120), blockCount);

				using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
				{
					await wallet.InitializeAsync(cts.Token); // Initialize wallet service.
				}

				var waitCount = 0;
				while (wallet.Coins.Sum(x => x.Amount) == Money.Zero)
				{
					await Task.Delay(1000);
					waitCount++;
					if (waitCount >= 21)
					{
						throw new TimeoutException("Funding transaction to the wallet did not arrive.");
					}
				}

				var scp = new Key().ScriptPubKey;
				var res2 = await wallet.BuildTransactionAsync("password", new[] { new WalletService.Operation(scp, new Money(0.05m, MoneyUnit.BTC), "foo") }, 5, false);

				Assert.NotNull(res2.Transaction);
				Assert.Single(res2.OuterWalletOutputs);
				Assert.Equal(scp, res2.OuterWalletOutputs.Single().ScriptPubKey);
				Assert.Single(res2.InnerWalletOutputs);
				Assert.True(res2.Fee > new Money(5 * 100)); // since there is a sanity check of 5sat/b in the server
				Assert.InRange(res2.FeePercentOfSent, 0, 1);
				Assert.Single(res2.SpentCoins);
				Assert.Equal(key.GetP2wpkhScript(), res2.SpentCoins.Single().ScriptPubKey);
				Assert.Equal(new Money(1m, MoneyUnit.BTC), res2.SpentCoins.Single().Amount);
				Assert.False(res2.SpendsUnconfirmed);

				await wallet.SendTransactionAsync(res2.Transaction);

				Assert.Contains(res2.InnerWalletOutputs.Single(), wallet.Coins);

				#region Basic

				Script receive = wallet.GetReceiveKey("Basic").GetP2wpkhScript();
				Money amountToSend = wallet.Coins.Where(x => !x.SpentOrLocked).Sum(x => x.Amount) / 2;
				var res = await wallet.BuildTransactionAsync("password", new[] { new WalletService.Operation(receive, amountToSend, "foo") }, 1008, allowUnconfirmed: true);

				Assert.Equal(2, res.InnerWalletOutputs.Count());
				Assert.Empty(res.OuterWalletOutputs);
				var activeOutput = res.InnerWalletOutputs.Single(x => x.ScriptPubKey == receive);
				var changeOutput = res.InnerWalletOutputs.Single(x => x.ScriptPubKey != receive);

				Assert.Equal(receive, activeOutput.ScriptPubKey);
				Assert.Equal(amountToSend, activeOutput.Amount);
				if (res.SpentCoins.Sum(x => x.Amount) - activeOutput.Amount == res.Fee) // this happens when change is too small
				{
					Assert.Contains(res.Transaction.Transaction.Outputs, x => x.Value == activeOutput.Amount);
					Logger.LogInfo<RegTests>($"Change Output: {changeOutput.Amount.ToString(false, true)} {changeOutput.ScriptPubKey.GetDestinationAddress(network)}");
				}
				Logger.LogInfo<RegTests>($"Fee: {res.Fee}");
				Logger.LogInfo<RegTests>($"FeePercentOfSent: {res.FeePercentOfSent} %");
				Logger.LogInfo<RegTests>($"SpendsUnconfirmed: {res.SpendsUnconfirmed}");
				Logger.LogInfo<RegTests>($"Active Output: {activeOutput.Amount.ToString(false, true)} {activeOutput.ScriptPubKey.GetDestinationAddress(network)}");
				Logger.LogInfo<RegTests>($"TxId: {res.Transaction.GetHash()}");

				var foundReceive = false;
				Assert.InRange(res.Transaction.Transaction.Outputs.Count, 1, 2);
				foreach (var output in res.Transaction.Transaction.Outputs)
				{
					if (output.ScriptPubKey == receive)
					{
						foundReceive = true;
						Assert.Equal(amountToSend, output.Value);
					}
				}
				Assert.True(foundReceive);

				await wallet.SendTransactionAsync(res.Transaction);

				#endregion

				#region SubtractFeeFromAmount

				receive = wallet.GetReceiveKey("SubtractFeeFromAmount").GetP2wpkhScript();
				amountToSend = wallet.Coins.Where(x => !x.SpentOrLocked).Sum(x => x.Amount) / 2;
				res = await wallet.BuildTransactionAsync("password", new[] { new WalletService.Operation(receive, amountToSend, "foo") }, 1008, allowUnconfirmed: true, subtractFeeFromAmountIndex: 0);

				Assert.Equal(2, res.InnerWalletOutputs.Count());
				Assert.Empty(res.OuterWalletOutputs);
				activeOutput = res.InnerWalletOutputs.Single(x => x.ScriptPubKey == receive);
				changeOutput = res.InnerWalletOutputs.Single(x => x.ScriptPubKey != receive);

				Assert.Equal(receive, activeOutput.ScriptPubKey);
				Assert.Equal(amountToSend - res.Fee, activeOutput.Amount);
				Assert.Contains(res.Transaction.Transaction.Outputs, x => x.Value == changeOutput.Amount);
				Logger.LogInfo<RegTests>($"Fee: {res.Fee}");
				Logger.LogInfo<RegTests>($"FeePercentOfSent: {res.FeePercentOfSent} %");
				Logger.LogInfo<RegTests>($"SpendsUnconfirmed: {res.SpendsUnconfirmed}");
				Logger.LogInfo<RegTests>($"Active Output: {activeOutput.Amount.ToString(false, true)} {activeOutput.ScriptPubKey.GetDestinationAddress(network)}");
				Logger.LogInfo<RegTests>($"Change Output: {changeOutput.Amount.ToString(false, true)} {changeOutput.ScriptPubKey.GetDestinationAddress(network)}");
				Logger.LogInfo<RegTests>($"TxId: {res.Transaction.GetHash()}");

				foundReceive = false;
				Assert.InRange(res.Transaction.Transaction.Outputs.Count, 1, 2);
				foreach (var output in res.Transaction.Transaction.Outputs)
				{
					if (output.ScriptPubKey == receive)
					{
						foundReceive = true;
						Assert.Equal(amountToSend - res.Fee, output.Value);
					}
				}
				Assert.True(foundReceive);

				#endregion

				#region LowFee

				res = await wallet.BuildTransactionAsync("password", new[] { new WalletService.Operation(receive, amountToSend, "foo") }, 1008, allowUnconfirmed: true);

				Assert.Equal(2, res.InnerWalletOutputs.Count());
				Assert.Empty(res.OuterWalletOutputs);
				activeOutput = res.InnerWalletOutputs.Single(x => x.ScriptPubKey == receive);
				changeOutput = res.InnerWalletOutputs.Single(x => x.ScriptPubKey != receive);

				Assert.Equal(receive, activeOutput.ScriptPubKey);
				Assert.Equal(amountToSend, activeOutput.Amount);
				Assert.Contains(res.Transaction.Transaction.Outputs, x => x.Value == changeOutput.Amount);
				Logger.LogInfo<RegTests>($"Fee: {res.Fee}");
				Logger.LogInfo<RegTests>($"FeePercentOfSent: {res.FeePercentOfSent} %");
				Logger.LogInfo<RegTests>($"SpendsUnconfirmed: {res.SpendsUnconfirmed}");
				Logger.LogInfo<RegTests>($"Active Output: {activeOutput.Amount.ToString(false, true)} {activeOutput.ScriptPubKey.GetDestinationAddress(network)}");
				Logger.LogInfo<RegTests>($"Change Output: {changeOutput.Amount.ToString(false, true)} {changeOutput.ScriptPubKey.GetDestinationAddress(network)}");
				Logger.LogInfo<RegTests>($"TxId: {res.Transaction.GetHash()}");

				foundReceive = false;
				Assert.InRange(res.Transaction.Transaction.Outputs.Count, 1, 2);
				foreach (var output in res.Transaction.Transaction.Outputs)
				{
					if (output.ScriptPubKey == receive)
					{
						foundReceive = true;
						Assert.Equal(amountToSend, output.Value);
					}
				}
				Assert.True(foundReceive);

				#endregion

				#region MediumFee

				res = await wallet.BuildTransactionAsync("password", new[] { new WalletService.Operation(receive, amountToSend, "foo") }, 144, allowUnconfirmed: true);

				Assert.Equal(2, res.InnerWalletOutputs.Count());
				Assert.Empty(res.OuterWalletOutputs);
				activeOutput = res.InnerWalletOutputs.Single(x => x.ScriptPubKey == receive);
				changeOutput = res.InnerWalletOutputs.Single(x => x.ScriptPubKey != receive);

				Assert.Equal(receive, activeOutput.ScriptPubKey);
				Assert.Equal(amountToSend, activeOutput.Amount);
				Assert.Contains(res.Transaction.Transaction.Outputs, x => x.Value == changeOutput.Amount);
				Logger.LogInfo<RegTests>($"Fee: {res.Fee}");
				Logger.LogInfo<RegTests>($"FeePercentOfSent: {res.FeePercentOfSent} %");
				Logger.LogInfo<RegTests>($"SpendsUnconfirmed: {res.SpendsUnconfirmed}");
				Logger.LogInfo<RegTests>($"Active Output: {activeOutput.Amount.ToString(false, true)} {activeOutput.ScriptPubKey.GetDestinationAddress(network)}");
				Logger.LogInfo<RegTests>($"Change Output: {changeOutput.Amount.ToString(false, true)} {changeOutput.ScriptPubKey.GetDestinationAddress(network)}");
				Logger.LogInfo<RegTests>($"TxId: {res.Transaction.GetHash()}");

				foundReceive = false;
				Assert.InRange(res.Transaction.Transaction.Outputs.Count, 1, 2);
				foreach (var output in res.Transaction.Transaction.Outputs)
				{
					if (output.ScriptPubKey == receive)
					{
						foundReceive = true;
						Assert.Equal(amountToSend, output.Value);
					}
				}
				Assert.True(foundReceive);

				#endregion

				#region HighFee

				res = await wallet.BuildTransactionAsync("password", new[] { new WalletService.Operation(receive, amountToSend, "foo") }, 2, allowUnconfirmed: true);

				Assert.Equal(2, res.InnerWalletOutputs.Count());
				Assert.Empty(res.OuterWalletOutputs);
				activeOutput = res.InnerWalletOutputs.Single(x => x.ScriptPubKey == receive);
				changeOutput = res.InnerWalletOutputs.Single(x => x.ScriptPubKey != receive);

				Assert.Equal(receive, activeOutput.ScriptPubKey);
				Assert.Equal(amountToSend, activeOutput.Amount);
				Assert.Contains(res.Transaction.Transaction.Outputs, x => x.Value == changeOutput.Amount);
				Logger.LogInfo<RegTests>($"Fee: {res.Fee}");
				Logger.LogInfo<RegTests>($"FeePercentOfSent: {res.FeePercentOfSent} %");
				Logger.LogInfo<RegTests>($"SpendsUnconfirmed: {res.SpendsUnconfirmed}");
				Logger.LogInfo<RegTests>($"Active Output: {activeOutput.Amount.ToString(false, true)} {activeOutput.ScriptPubKey.GetDestinationAddress(network)}");
				Logger.LogInfo<RegTests>($"Change Output: {changeOutput.Amount.ToString(false, true)} {changeOutput.ScriptPubKey.GetDestinationAddress(network)}");
				Logger.LogInfo<RegTests>($"TxId: {res.Transaction.GetHash()}");

				foundReceive = false;
				Assert.InRange(res.Transaction.Transaction.Outputs.Count, 1, 2);
				foreach (var output in res.Transaction.Transaction.Outputs)
				{
					if (output.ScriptPubKey == receive)
					{
						foundReceive = true;
						Assert.Equal(amountToSend, output.Value);
					}
				}
				Assert.True(foundReceive);

				Assert.InRange(res.Fee, Money.Zero, res.Fee);
				Assert.InRange(res.Fee, res.Fee, res.Fee);

				await wallet.SendTransactionAsync(res.Transaction);

				#endregion

				#region MaxAmount

				receive = wallet.GetReceiveKey("MaxAmount").GetP2wpkhScript();
				res = await wallet.BuildTransactionAsync("password", new[] { new WalletService.Operation(receive, Money.Zero, "foo") }, 1008, allowUnconfirmed: true);

				Assert.Single(res.InnerWalletOutputs);
				Assert.Empty(res.OuterWalletOutputs);
				activeOutput = res.InnerWalletOutputs.Single();

				Assert.Equal(receive, activeOutput.ScriptPubKey);

				Logger.LogInfo<RegTests>($"Fee: {res.Fee}");
				Logger.LogInfo<RegTests>($"FeePercentOfSent: {res.FeePercentOfSent} %");
				Logger.LogInfo<RegTests>($"SpendsUnconfirmed: {res.SpendsUnconfirmed}");
				Logger.LogInfo<RegTests>($"Active Output: {activeOutput.Amount.ToString(false, true)} {activeOutput.ScriptPubKey.GetDestinationAddress(network)}");
				Logger.LogInfo<RegTests>($"TxId: {res.Transaction.GetHash()}");

				Assert.Single(res.Transaction.Transaction.Outputs);
				var maxBuiltTxOutput = res.Transaction.Transaction.Outputs.Single();
				Assert.Equal(receive, maxBuiltTxOutput.ScriptPubKey);
				Assert.Equal(wallet.Coins.Where(x => !x.SpentOrLocked).Sum(x => x.Amount) - res.Fee, maxBuiltTxOutput.Value);

				await wallet.SendTransactionAsync(res.Transaction);

				#endregion

				#region InputSelection

				receive = wallet.GetReceiveKey("InputSelection").GetP2wpkhScript();

				var inputCountBefore = res.SpentCoins.Count();
				res = await wallet.BuildTransactionAsync("password", new[] { new WalletService.Operation(receive, Money.Zero, "foo") }, 1008,
					allowUnconfirmed: true,
					allowedInputs: wallet.Coins.Where(x => !x.SpentOrLocked).Select(x => new TxoRef(x.TransactionId, x.Index)).Take(1));

				Assert.Single(res.InnerWalletOutputs);
				Assert.Empty(res.OuterWalletOutputs);
				activeOutput = res.InnerWalletOutputs.Single(x => x.ScriptPubKey == receive);

				Assert.True(inputCountBefore >= res.SpentCoins.Count());
				Assert.Equal(res.SpentCoins.Count(), res.Transaction.Transaction.Inputs.Count);

				Assert.Equal(receive, activeOutput.ScriptPubKey);
				Logger.LogInfo<RegTests>($"Fee: {res.Fee}");
				Logger.LogInfo<RegTests>($"FeePercentOfSent: {res.FeePercentOfSent} %");
				Logger.LogInfo<RegTests>($"SpendsUnconfirmed: {res.SpendsUnconfirmed}");
				Logger.LogInfo<RegTests>($"Active Output: {activeOutput.Amount.ToString(false, true)} {activeOutput.ScriptPubKey.GetDestinationAddress(network)}");
				Logger.LogInfo<RegTests>($"TxId: {res.Transaction.GetHash()}");

				Assert.Single(res.Transaction.Transaction.Outputs);

				res = await wallet.BuildTransactionAsync("password", new[] { new WalletService.Operation(receive, Money.Zero, "foo") }, 1008,
					allowUnconfirmed: true,
					allowedInputs: new[] { res.SpentCoins.Select(x => new TxoRef(x.TransactionId, x.Index)).First() });

				Assert.Single(res.InnerWalletOutputs);
				Assert.Empty(res.OuterWalletOutputs);
				activeOutput = res.InnerWalletOutputs.Single(x => x.ScriptPubKey == receive);

				Assert.Single(res.Transaction.Transaction.Inputs);
				Assert.Single(res.Transaction.Transaction.Outputs);
				Assert.Single(res.SpentCoins);

				#endregion

				#region Labeling

				res = await wallet.BuildTransactionAsync("password", new[] { new WalletService.Operation(receive, Money.Zero, "my label") }, 1008,
					allowUnconfirmed: true);

				Assert.Single(res.InnerWalletOutputs);
				Assert.Equal("change of (my label)", res.InnerWalletOutputs.Single().Label);

				amountToSend = wallet.Coins.Where(x => !x.SpentOrLocked).Sum(x => x.Amount) / 3;
				res = await wallet.BuildTransactionAsync("password", new[] {
					new WalletService.Operation(new Key().ScriptPubKey, amountToSend, "outgoing"),
					new WalletService.Operation(new Key().ScriptPubKey, amountToSend, "outgoing2")
				}, 1008,
					allowUnconfirmed: true);

				Assert.Single(res.InnerWalletOutputs);
				Assert.Equal(2, res.OuterWalletOutputs.Count());
				Assert.Equal("change of (outgoing, outgoing2)", res.InnerWalletOutputs.Single().Label);

				await wallet.SendTransactionAsync(res.Transaction);

				Assert.Contains("change of (outgoing, outgoing2)", wallet.Coins.Where(x => x.Height == Height.MemPool).Select(x => x.Label));
				Assert.Contains("change of (outgoing, outgoing2)", keyManager.GetKeys().Select(x => x.Label));

				_filtersProcessedByWalletCount = 0;
				await Global.RpcClient.GenerateAsync(1);
				await WaitForFiltersToBeProcessedAsync(TimeSpan.FromSeconds(120), 1);

				var bestHeight = wallet.IndexDownloader.GetBestFilter().BlockHeight;
				Assert.Contains("change of (outgoing, outgoing2)", wallet.Coins.Where(x => x.Height == bestHeight).Select(x => x.Label));
				Assert.Contains("change of (outgoing, outgoing2)", keyManager.GetKeys().Select(x => x.Label));

				#endregion
			}
			finally
			{
				wallet.NewFilterProcessed -= Wallet_NewFilterProcessed;
				wallet?.Dispose();
				// Dispose index downloader service.
				if (indexDownloader != null)
				{
					await indexDownloader.StopAsync();
				}
				// Dispose connection service.
				nodes?.Dispose();
				// Dispose chaumian coinjoin client.
				if (chaumianClient != null)
				{
					await chaumianClient.StopAsync();
				}
			}
		}

		[Fact]
		public async Task BuildTransactionValidationsTestAsync()
		{
			// Make sure fitlers are created on the server side.
			await AssertFiltersInitializedAsync();

			var network = Global.RpcClient.Network;

			// Create the services.
			// 1. Create connection service.
			var nodes = new NodesGroup(Global.Config.Network,
					requirements: new NodeRequirement
					{
						RequiredServices = NodeServices.Network,
						MinVersion = ProtocolVersion_WITNESS_VERSION
					});
			nodes.ConnectedNodes.Add(RegTestFixture.BackendRegTestNode.CreateNodeClient());

			// 2. Create mempool service.
			var memPoolService = new MemPoolService();
			Node node = RegTestFixture.BackendRegTestNode.CreateNodeClient();
			node.Behaviors.Add(new MemPoolBehavior(memPoolService));

			// 3. Create index downloader service.
			var indexFilePath = Path.Combine(SharedFixture.DataDir, nameof(SendTestsFromHiddenWalletAsync), $"Index{Global.RpcClient.Network}.dat");
			var indexDownloader = new IndexDownloader(Global.RpcClient.Network, indexFilePath, new Uri(RegTestFixture.BackendEndPoint));

			// 4. Create key manager service.
			var keyManager = KeyManager.CreateNew(out _, "password");

			// 5. Create chaumian coinjoin client.
			var chaumianClient = new CcjClient(Global.RpcClient.Network, Global.Coordinator.RsaKey.PubKey, keyManager, new Uri(RegTestFixture.BackendEndPoint));

			// 6. Create wallet service.
			var blocksFolderPath = Path.Combine(SharedFixture.DataDir, nameof(SendTestsFromHiddenWalletAsync), $"Blocks");
			var wallet = new WalletService(keyManager, indexDownloader, chaumianClient, memPoolService, nodes, blocksFolderPath);
			wallet.NewFilterProcessed += Wallet_NewFilterProcessed;

			var scp = new Key().ScriptPubKey;
			var validOperationList = new[] { new WalletService.Operation(scp, Money.Coins(1), "") };
			var invalidOperationList = new[] { new WalletService.Operation(scp, Money.Coins(10 * 1000 * 1000), ""), new WalletService.Operation(scp, Money.Coins(12 * 1000 * 1000), "") };
			var overflowOperationList = new[]{
				new WalletService.Operation(scp, Money.Satoshis(long.MaxValue), ""),
				new WalletService.Operation(scp, Money.Satoshis(long.MaxValue), ""),
				new WalletService.Operation(scp, Money.Satoshis(5), "")
				};

			Logger.TurnOff();
			// toSend cannot be null
			await Assert.ThrowsAsync<ArgumentNullException>(async () => await wallet.BuildTransactionAsync(null, null, 0));

			// toSend cannot have a null element
			await Assert.ThrowsAsync<ArgumentNullException>(async () => await wallet.BuildTransactionAsync(null, new[] { (WalletService.Operation)null }, 0));

			// toSend cannot have a zero elements
			await Assert.ThrowsAsync<ArgumentException>(async () => await wallet.BuildTransactionAsync(null, new WalletService.Operation[0], 0));

			// feeTarget has to be in the range 0 to 1008
			await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await wallet.BuildTransactionAsync(null, validOperationList, -10));
			await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await wallet.BuildTransactionAsync(null, validOperationList, 2000));

			// subtractFeeFromAmountIndex has to be valid
			await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await wallet.BuildTransactionAsync(null, validOperationList, 2, false, -10));
			await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await wallet.BuildTransactionAsync(null, validOperationList, 2, false, 1));

			// toSend amount sum has to be in range 0 to 2099999997690000
			await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await wallet.BuildTransactionAsync(null, invalidOperationList, 2));

			// toSend negative sum amount
			var operations = new[]{
				new WalletService.Operation(scp, -10000, "") };
			await Assert.ThrowsAsync<ArgumentException>(async () => await wallet.BuildTransactionAsync(null, operations, 2));

			// toSend negative operation amount
			operations = new[]{
				new WalletService.Operation(scp,  20000, ""),
				new WalletService.Operation(scp, -10000, "") };
			await Assert.ThrowsAsync<ArgumentException>(async () => await wallet.BuildTransactionAsync(null, operations, 2));

			// toSend ammount sum has to be less than ulong.MaxValue
			await Assert.ThrowsAsync<OverflowException>(async () => await wallet.BuildTransactionAsync(null, overflowOperationList, 2));

			// allowedInputs cannot be empty
			await Assert.ThrowsAsync<ArgumentException>(async () => await wallet.BuildTransactionAsync(null, validOperationList, 2, false, null, null, new TxoRef[0]));

			// Get some money, make it confirm.
			var key = wallet.GetReceiveKey("foo label");
			var txid = await Global.RpcClient.SendToAddressAsync(key.GetP2wpkhAddress(network), new Money(1m, MoneyUnit.BTC));

			// Generate some coins
			await Global.RpcClient.GenerateAsync(2);

			try
			{
				nodes.Connect(); // Start connection service.
				node.VersionHandshake(); // Start mempool service.
				indexDownloader.Synchronize(requestInterval: TimeSpan.FromSeconds(3)); // Start index downloader service.
				chaumianClient.Start(); // Start chaumian coinjoin client.

				// Wait until the filter our previous transaction is present.
				var blockCount = await Global.RpcClient.GetBlockCountAsync();
				await WaitForFiltersToBeProcessedAsync(TimeSpan.FromSeconds(120), blockCount);

				using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
				{
					await wallet.InitializeAsync(cts.Token); // Initialize wallet service.
				}

				// subtract Fee from amount index with no enough money 
				operations = new[]{
					new WalletService.Operation(scp,  Money.Satoshis(1m), ""),
					new WalletService.Operation(scp, Money.Coins(0.5m), "") };
				await Assert.ThrowsAsync<InsufficientBalanceException>(async () => await wallet.BuildTransactionAsync("password", operations, 2, false, 0));

				// No enough money (only one confirmed coin, no unconfirmed allowed)
				operations = new[] { new WalletService.Operation(scp, Money.Coins(1.5m), "") };
				await Assert.ThrowsAsync<InsufficientBalanceException>(async () => await wallet.BuildTransactionAsync(null, operations, 2));

				// No enough money (only one confirmed coin, unconfirmed allowed)
				await Assert.ThrowsAsync<InsufficientBalanceException>(async () => await wallet.BuildTransactionAsync(null, operations, 2, true));

				// Add new money with no confirmation
				var txid2 = await Global.RpcClient.SendToAddressAsync(key.GetP2wpkhAddress(network), new Money(1m, MoneyUnit.BTC));
				await Task.Delay(1000); // Wait tx to arrive and get processed.

				// Enough money (one confirmed coin and one unconfirmed coin, unconfirmed are NOT allowed)
				await Assert.ThrowsAsync<InsufficientBalanceException>(async () => await wallet.BuildTransactionAsync(null, operations, 2, false));

				// Enough money (one confirmed coin and one unconfirmed coin, unconfirmed are allowed)
				var btx = await wallet.BuildTransactionAsync("password", operations, 2, true);
				Assert.Equal(2, btx.SpentCoins.Count());
				Assert.Equal(1, btx.SpentCoins.Count(c => c.Confirmed == true));
				Assert.Equal(1, btx.SpentCoins.Count(c => c.Confirmed == false));

				// Only one operation with Zero money
				operations = new[]{
					new WalletService.Operation(scp, Money.Zero, ""),
					new WalletService.Operation(scp, Money.Zero, "") };
				await Assert.ThrowsAsync<ArgumentException>(async () => await wallet.BuildTransactionAsync(null, operations, 2));

				// `Custom change` and `spend all` cannot be specified at the same time
				await Assert.ThrowsAsync<ArgumentException>(async () => await wallet.BuildTransactionAsync(null, operations, 2, false, null, Script.Empty));
				Logger.TurnOn();

				operations = new[] { new WalletService.Operation(scp, Money.Coins(0.5m), "") };
				btx = await wallet.BuildTransactionAsync("password", operations, 2);

				operations = new[] { new WalletService.Operation(scp, Money.Coins(0.00005m), "") };
				btx = await wallet.BuildTransactionAsync("password", operations, 2, false, 0);
				Assert.True(btx.FeePercentOfSent > 20);
				Assert.Single(btx.SpentCoins);
				Assert.Equal(txid, btx.SpentCoins.First().TransactionId);
				Assert.False(btx.Transaction.Transaction.RBF);
			}
			finally
			{
				wallet?.Dispose();
				// Dispose index downloader service.
				if (indexDownloader != null)
				{
					await indexDownloader.StopAsync();
				}
				// Dispose connection service.
				nodes?.Dispose();
				// Dispose chaumian coinjoin client.
				if (chaumianClient != null)
				{
					await chaumianClient.StopAsync();
				}
			}
		}

		[Fact]
		public async Task BuildTransactionReorgsTestAsync()
		{
			// Make sure fitlers are created on the server side.
			await AssertFiltersInitializedAsync();

			var network = Global.RpcClient.Network;

			// Create the services.
			// 1. Create connection service.
			var nodes = new NodesGroup(Global.Config.Network,
					requirements: new NodeRequirement
					{
						RequiredServices = NodeServices.Network,
						MinVersion = ProtocolVersion_WITNESS_VERSION
					});
			nodes.ConnectedNodes.Add(RegTestFixture.BackendRegTestNode.CreateNodeClient());

			// 2. Create mempool service.
			var memPoolService = new MemPoolService();
			Node node = RegTestFixture.BackendRegTestNode.CreateNodeClient();
			node.Behaviors.Add(new MemPoolBehavior(memPoolService));

			// 3. Create index downloader service.
			var indexFilePath = Path.Combine(SharedFixture.DataDir, nameof(SendTestsFromHiddenWalletAsync), $"Index{Global.RpcClient.Network}.dat");
			var indexDownloader = new IndexDownloader(Global.RpcClient.Network, indexFilePath, new Uri(RegTestFixture.BackendEndPoint));

			// 4. Create key manager service.
			var keyManager = KeyManager.CreateNew(out _, "password");

			// 5. Create chaumian coinjoin client.
			var chaumianClient = new CcjClient(Global.RpcClient.Network, Global.Coordinator.RsaKey.PubKey, keyManager, new Uri(RegTestFixture.BackendEndPoint));

			// 6. Create wallet service.
			var blocksFolderPath = Path.Combine(SharedFixture.DataDir, nameof(SendTestsFromHiddenWalletAsync), $"Blocks");
			var wallet = new WalletService(keyManager, indexDownloader, chaumianClient, memPoolService, nodes, blocksFolderPath);
			wallet.NewFilterProcessed += Wallet_NewFilterProcessed;

			Assert.Empty(wallet.Coins);
			var baseTip = await Global.RpcClient.GetBestBlockHashAsync();

			// Generate script
			var scp = new Key().ScriptPubKey;

			// Get some money, make it confirm.
			var key = wallet.GetReceiveKey("foo label");
			var fundingTxid = await Global.RpcClient.SendToAddressAsync(key.GetP2wpkhAddress(network), new Money(0.1m, MoneyUnit.BTC));

			// Generate some coins
			await Global.RpcClient.GenerateAsync(2);

			try
			{
				nodes.Connect(); // Start connection service.
				node.VersionHandshake(); // Start mempool service.
				indexDownloader.Synchronize(requestInterval: TimeSpan.FromSeconds(3)); // Start index downloader service.
				chaumianClient.Start(); // Start chaumian coinjoin client.

				// Wait until the filter our previous transaction is present.
				var blockCount = await Global.RpcClient.GetBlockCountAsync();
				await WaitForFiltersToBeProcessedAsync(TimeSpan.FromSeconds(120), blockCount);

				using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
				{
					await wallet.InitializeAsync(cts.Token); // Initialize wallet service.
				}
				Assert.Single(wallet.Coins);

				// Send money before reorg.
				var operations = new[]{
					new WalletService.Operation(scp, Money.Coins(0.011m), "") };
				var btx1 = await wallet.BuildTransactionAsync("password", operations, 2);
				await wallet.SendTransactionAsync(btx1.Transaction);

				operations = new[]{
					new WalletService.Operation(scp, Money.Coins(0.012m), "") };
				var btx2 = await wallet.BuildTransactionAsync("password", operations, 2, allowUnconfirmed: true);
				await wallet.SendTransactionAsync(btx2.Transaction);

				// Test synchronization after fork.
				// Invalidate the blocks containing the funding transaction
				var tip = await Global.RpcClient.GetBestBlockHashAsync();
				await Global.RpcClient.InvalidateBlockAsync(tip); // Reorg 1
				tip = await Global.RpcClient.GetBestBlockHashAsync();
				await Global.RpcClient.InvalidateBlockAsync(tip); // Reorg 2

				// Generate three new blocks (replace the previous invalidated ones)
				_filtersProcessedByWalletCount = 0;
				await Global.RpcClient.GenerateAsync(3);
				await WaitForFiltersToBeProcessedAsync(TimeSpan.FromSeconds(120), 3);

				// Send money after reorg.
				// When we invalidate a block, those transactions setted in the invalidated block
				// are reintroduced when we generate a new block though the rpc call 
				operations = new[]{
					new WalletService.Operation(scp, Money.Coins(0.013m), "") };
				var btx3 = await wallet.BuildTransactionAsync("password", operations, 2);
				await wallet.SendTransactionAsync(btx3.Transaction);

				operations = new[]{
					new WalletService.Operation(scp, Money.Coins(0.014m), "") };
				var btx4 = await wallet.BuildTransactionAsync("password", operations, 2, allowUnconfirmed: true);
				await wallet.SendTransactionAsync(btx4.Transaction);

				// Test synchronization after fork with different transactions.
				// Create a fork that invalidates the blocks containing the funding transaction
				_filtersProcessedByWalletCount = 0;
				await Global.RpcClient.InvalidateBlockAsync(baseTip);
				try
				{
					await Global.RpcClient.SendCommandAsync("abandontransaction", fundingTxid.ToString());
				}
				catch
				{
					return; // Occassionally this fails on Linux or OSX, I have no idea why.
				}
				await Global.RpcClient.GenerateAsync(10);
				await WaitForFiltersToBeProcessedAsync(TimeSpan.FromSeconds(120), 10);

				var curBlockHash = await Global.RpcClient.GetBestBlockHashAsync();
				blockCount = await Global.RpcClient.GetBlockCountAsync();

				// Make sure the funding transaction is not in any block of the chain
				while (curBlockHash != Global.RpcClient.Network.GenesisHash)
				{
					var block = await Global.RpcClient.GetBlockAsync(curBlockHash);

					if (block.Transactions.Any(tx => tx.GetHash() == fundingTxid))
					{
						throw new Exception($"Transaction found in block at heigh {blockCount}  hash: {block.GetHash()}");
					}
					curBlockHash = block.Header.HashPrevBlock;
					blockCount--;
				}

				// There shouldn't be any `confirmed` coin
				Assert.Empty(wallet.Coins.Where(x => x.Confirmed));

				// Get some money, make it confirm.
				// this is necesary because we are in a fork now.
				fundingTxid = await Global.RpcClient.SendToAddressAsync(key.GetP2wpkhAddress(network), new Money(1m, MoneyUnit.BTC), replaceable: true);
				await Task.Delay(1000); // Waits for the funding transaction get to the mempool.
				Assert.Single(wallet.Coins.Where(x => !x.Confirmed));

				var fundingBumpTxid = await Global.RpcClient.BumpFeeAsync(fundingTxid);
				await Task.Delay(2000); // Waits for the funding transaction get to the mempool.
				Assert.Single(wallet.Coins.Where(x => !x.Confirmed));
				Assert.Single(wallet.Coins.Where(x => x.TransactionId == fundingBumpTxid.TransactionId));

				// Confirm the coin
				_filtersProcessedByWalletCount = 0;
				await Global.RpcClient.GenerateAsync(1);
				await WaitForFiltersToBeProcessedAsync(TimeSpan.FromSeconds(120), 1);

				Assert.Single(wallet.Coins.Where(x => x.Confirmed && x.TransactionId == fundingBumpTxid.TransactionId));
			}
			finally
			{
				wallet?.Dispose();
				// Dispose index downloader service.
				if (indexDownloader != null)
				{
					await indexDownloader.StopAsync();
				}
				// Dispose connection service.
				nodes?.Dispose();
				// Dispose chaumian coinjoin client.
				if (chaumianClient != null)
				{
					await chaumianClient.StopAsync();
				}
			}
		}

		[Fact]
		public async Task SpendUnconfirmedTxTestAsync()
		{
			// Make sure fitlers are created on the server side.
			await AssertFiltersInitializedAsync();

			var network = Global.RpcClient.Network;

			// Create the services.
			// 1. Create connection service.
			var nodes = new NodesGroup(Global.Config.Network,
				requirements: new NodeRequirement
				{
					RequiredServices = NodeServices.Network,
					MinVersion = ProtocolVersion_WITNESS_VERSION
				});
			nodes.ConnectedNodes.Add(RegTestFixture.BackendRegTestNode.CreateNodeClient());

			// 2. Create mempool service.
			var memPoolService = new MemPoolService();
			Node node = RegTestFixture.BackendRegTestNode.CreateNodeClient();
			node.Behaviors.Add(new MemPoolBehavior(memPoolService));

			// 3. Create index downloader service.
			var indexFilePath = Path.Combine(SharedFixture.DataDir, nameof(SendTestsFromHiddenWalletAsync),
				$"Index{Global.RpcClient.Network}.dat");
			var indexDownloader =
				new IndexDownloader(Global.RpcClient.Network, indexFilePath, new Uri(RegTestFixture.BackendEndPoint));

			// 4. Create key manager service.
			var keyManager = KeyManager.CreateNew(out _, "password");

			// 5. Create chaumian coinjoin client.
			var chaumianClient = new CcjClient(Global.RpcClient.Network, Global.Coordinator.RsaKey.PubKey, keyManager, new Uri(RegTestFixture.BackendEndPoint));

			// 6. Create wallet service.
			var blocksFolderPath = Path.Combine(SharedFixture.DataDir, nameof(SendTestsFromHiddenWalletAsync), $"Blocks");
			var wallet = new WalletService(keyManager, indexDownloader, chaumianClient, memPoolService, nodes, blocksFolderPath);
			wallet.NewFilterProcessed += Wallet_NewFilterProcessed;

			Assert.Empty(wallet.Coins);

			// Get some money, make it confirm.
			var key = wallet.GetReceiveKey("foo label");

			try
			{
				nodes.Connect(); // Start connection service.
				node.VersionHandshake(); // Start mempool service.
				indexDownloader.Synchronize(requestInterval: TimeSpan.FromSeconds(3)); // Start index downloader service.
				chaumianClient.Start(); // Start chaumian coinjoin client.

				// Wait until the filter our previous transaction is present.
				var blockCount = await Global.RpcClient.GetBlockCountAsync();
				await WaitForFiltersToBeProcessedAsync(TimeSpan.FromSeconds(120), blockCount);

				using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
				{
					await wallet.InitializeAsync(cts.Token); // Initialize wallet service.
				}

				Assert.Empty(wallet.Coins);
				
				// Get some money, make it confirm.
				// this is necesary because we are in a fork now.
				var tx0Id = await Global.RpcClient.SendToAddressAsync(key.GetP2wpkhAddress(network), new Money(1m, MoneyUnit.BTC),
					replaceable: true);
				while (wallet.Coins.Count == 0)
					await Task.Delay(500); // Waits for the funding transaction get to the mempool.
				Assert.Single(wallet.Coins);

				// Spend the unconfirmed coin (send it to ourself)
				var operations = new[] { new WalletService.Operation(key.PubKey.WitHash.ScriptPubKey, Money.Coins(0.5m), "") };
				var tx1Res = await wallet.BuildTransactionAsync("password", operations, 2, allowUnconfirmed: true);
				await wallet.SendTransactionAsync(tx1Res.Transaction);

				while (wallet.Coins.Count != 3)
					await Task.Delay(500); // Waits for the funding transaction get to the mempool.

				// There is a coin created by the latest spending transaction
				Assert.Contains(wallet.Coins, x => x.TransactionId == tx1Res.Transaction.GetHash());

				// There is a coin destroyed
				Assert.Equal(1, wallet.Coins.Count(x => x.SpentOrLocked && x.SpenderTransactionId == tx1Res.Transaction.GetHash()));

				// There is at least one coin created from the destruction of the first coin
				Assert.Contains(wallet.Coins, x => x.SpentOutputs.Any(o => o.TransactionId == tx0Id));

				var totalWallet = wallet.Coins.Where(c => !c.SpentOrLocked).Sum(c => c.Amount);
				Assert.Equal((1 * Money.COIN) - tx1Res.Fee.Satoshi, totalWallet);


				// Spend the unconfirmed and unspent coin (send it to ourself)
				operations = new[] { new WalletService.Operation(key.PubKey.WitHash.ScriptPubKey, Money.Coins(0.5m), "") };
				var tx2Res = await wallet.BuildTransactionAsync("password", operations, 2, allowUnconfirmed: true, subtractFeeFromAmountIndex: 0);
				await wallet.SendTransactionAsync(tx2Res.Transaction);

				while (wallet.Coins.Count != 4)
					await Task.Delay(500); // Waits for the transaction get to the mempool.

				// There is a coin created by the latest spending transaction
				Assert.Contains(wallet.Coins, x => x.TransactionId == tx2Res.Transaction.GetHash());

				// There is a coin destroyed
				Assert.Equal(1, wallet.Coins.Count(x => x.SpentOrLocked && x.SpenderTransactionId == tx2Res.Transaction.GetHash()));

				// There is at least one coin created from the destruction of the first coin
				Assert.Contains(wallet.Coins, x => x.SpentOutputs.Any(o => o.TransactionId == tx1Res.Transaction.GetHash()));

				totalWallet = wallet.Coins.Where(c => !c.SpentOrLocked).Sum(c => c.Amount);
				Assert.Equal((1 * Money.COIN) - tx1Res.Fee.Satoshi - tx2Res.Fee.Satoshi, totalWallet);

				_filtersProcessedByWalletCount = 0;
				var blockId = (await Global.RpcClient.GenerateAsync(1)).Single();
				await WaitForFiltersToBeProcessedAsync(TimeSpan.FromSeconds(120), 1);

				// Verify transactions are confirmed in the blockchain
				var block = await Global.RpcClient.GetBlockAsync(blockId);
				Assert.Contains(block.Transactions, x => x.GetHash() == tx2Res.Transaction.GetHash());
				Assert.Contains(block.Transactions, x => x.GetHash() == tx1Res.Transaction.GetHash());
				Assert.Contains(block.Transactions, x => x.GetHash() == tx0Id);

				Assert.True(wallet.Coins.All(x => x.Confirmed));
			}
			finally
			{
				wallet?.Dispose();
				// Dispose index downloader service.
				if (indexDownloader != null)
				{
					await indexDownloader.StopAsync();
				}
				// Dispose connection service.
				nodes?.Dispose();
				// Dispose chaumian coinjoin client.
				if (chaumianClient != null)
				{
					await chaumianClient.StopAsync();
				}
			}
		}

		[Fact]
		public async Task CcjTestsAsync()
		{
			var rpc = Global.RpcClient;
			var network = Global.RpcClient.Network;
			var coordinator = Global.Coordinator;
			Money denomination = new Money(0.2m, MoneyUnit.BTC);
			decimal coordinatorFeePercent = 0.2m;
			int anonymitySet = 2;
			int connectionConfirmationTimeout = 50;
			var roundConfig = new CcjRoundConfig(denomination, 2, coordinatorFeePercent, anonymitySet, 100, connectionConfirmationTimeout, 50, 50, 1);
			coordinator.UpdateRoundConfig(roundConfig);
			coordinator.FailAllRoundsInInputRegistration();

			using (var torClient = new TorHttpClient(new Uri(RegTestFixture.BackendEndPoint)))
			using (var satoshiClient = new SatoshiClient(new Uri(RegTestFixture.BackendEndPoint)))
			using (var bobClient = new BobClient(new Uri(RegTestFixture.BackendEndPoint)))
			using (var aliceClient = new AliceClient(new Uri(RegTestFixture.BackendEndPoint)))
			{
				#region PostInputsGetStates
				// <-------------------------->
				// POST INPUTS and GET STATES tests
				// <-------------------------->

				IEnumerable<CcjRunningRoundState> states = await satoshiClient.GetAllRoundStatesAsync();
				Assert.Equal(2, states.Count());
				foreach (CcjRunningRoundState rs in states)
				{
					// Never changes.
					Assert.True(0 < rs.RoundId);
					Assert.Equal(new Money(0.00000544m, MoneyUnit.BTC), rs.FeePerInputs);
					Assert.Equal(new Money(0.00000264m, MoneyUnit.BTC), rs.FeePerOutputs);
					Assert.Equal(7, rs.MaximumInputCountPerPeer);
					// Changes per rounds.
					Assert.Equal(denomination, rs.Denomination);
					Assert.Equal(coordinatorFeePercent, rs.CoordinatorFeePercent);
					Assert.Equal(anonymitySet, rs.RequiredPeerCount);
					Assert.Equal(connectionConfirmationTimeout, rs.RegistrationTimeout);
					// Changes per phases.
					Assert.Equal(CcjRoundPhase.InputRegistration, rs.Phase);
					Assert.Equal(0, rs.RegisteredPeerCount);
				}

				// Inputs request tests
				var inputsRequest = new InputsRequest
				{
					BlindedOutputScriptHex = null,
					ChangeOutputScript = null,
					Inputs = null,
				};

				HttpRequestException httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostInputsAsync(inputsRequest));
				Assert.Equal($"{HttpStatusCode.BadRequest.ToReasonString()}\nInvalid request.", httpRequestException.Message);

				Script blindedOutputScript = new Script();
				byte[] changeOutputScript = new byte[] { };
				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostInputsAsync(blindedOutputScript, changeOutputScript, new InputProofModel { Input = new OutPoint(), Proof = "" }));
				Assert.Equal($"{HttpStatusCode.BadRequest.ToReasonString()}\nInvalid request.", httpRequestException.Message);

				inputsRequest.BlindedOutputScriptHex = "c";
				inputsRequest.ChangeOutputScript = "a";
				inputsRequest.Inputs = new List<InputProofModel> { new InputProofModel { Input = new OutPoint(uint256.One, 0), Proof = "b" } };
				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostInputsAsync(inputsRequest));
				Assert.Equal($"{HttpStatusCode.BadRequest.ToReasonString()}\nProvided input is not unspent.", httpRequestException.Message);

				var addr = await rpc.GetNewAddressAsync();
				var hash = await rpc.SendToAddressAsync(addr, new Money(0.01m, MoneyUnit.BTC));
				var tx = await rpc.GetRawTransactionAsync(hash);
				var index = tx.Outputs.GetIndex(addr.ScriptPubKey);

				inputsRequest.Inputs = new List<InputProofModel> { new InputProofModel { Input = new OutPoint(hash, index), Proof = "b" } };
				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostInputsAsync(inputsRequest));
				Assert.Equal($"{HttpStatusCode.BadRequest.ToReasonString()}\nProvided input is neither confirmed, nor is from an unconfirmed coinjoin.", httpRequestException.Message);
				
				var blocks = await rpc.GenerateAsync(1);
				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostInputsAsync(inputsRequest));
				Assert.Equal($"{HttpStatusCode.BadRequest.ToReasonString()}\nProvided input must be witness_v0_keyhash.", httpRequestException.Message);

				var blockHash = blocks.Single();
				var block = await rpc.GetBlockAsync(blockHash);
				var coinbase = block.Transactions.First();
				inputsRequest.Inputs = new List<InputProofModel> { new InputProofModel { Input = new OutPoint(coinbase.GetHash(), 0), Proof = "b" } };
				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostInputsAsync(inputsRequest));
				Assert.Equal($"{HttpStatusCode.BadRequest.ToReasonString()}\nProvided input is immature.", httpRequestException.Message);

				var key = new Key();
				var witnessAddress = key.PubKey.GetSegwitAddress(network);
				hash = await rpc.SendToAddressAsync(witnessAddress, new Money(0.01m, MoneyUnit.BTC));
				await rpc.GenerateAsync(1);
				tx = await rpc.GetRawTransactionAsync(hash);
				index = tx.Outputs.GetIndex(witnessAddress.ScriptPubKey);
				inputsRequest.Inputs = new List<InputProofModel> { new InputProofModel { Input = new OutPoint(hash, index), Proof = "b" } };
				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostInputsAsync(inputsRequest));
				Assert.StartsWith($"{HttpStatusCode.BadRequest.ToReasonString()}", httpRequestException.Message);

				var proof = key.SignMessage("foo");
				inputsRequest.Inputs.First().Proof = proof;
				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostInputsAsync(inputsRequest));
				Assert.Equal($"{HttpStatusCode.BadRequest.ToReasonString()}\nProvided proof is invalid.", httpRequestException.Message);

				inputsRequest.BlindedOutputScriptHex = new Transaction().ToHex();
				proof = key.SignMessage(inputsRequest.BlindedOutputScriptHex);
				inputsRequest.Inputs.First().Proof = proof;
				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostInputsAsync(inputsRequest));
				Assert.StartsWith($"{HttpStatusCode.BadRequest.ToReasonString()}\nNot enough inputs are provided. Fee to pay:", httpRequestException.Message);

				roundConfig.Denomination = new Money(0.01m, MoneyUnit.BTC); // exactly the same as our output
				coordinator.UpdateRoundConfig(roundConfig);
				coordinator.FailAllRoundsInInputRegistration();
				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostInputsAsync(inputsRequest));
				Assert.StartsWith($"{HttpStatusCode.BadRequest.ToReasonString()}\nNot enough inputs are provided. Fee to pay:", httpRequestException.Message);

				roundConfig.Denomination = new Money(0.00999999m, MoneyUnit.BTC); // one satoshi less than our output
				coordinator.UpdateRoundConfig(roundConfig);
				coordinator.FailAllRoundsInInputRegistration();
				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostInputsAsync(inputsRequest));
				Assert.StartsWith($"{HttpStatusCode.BadRequest.ToReasonString()}\nNot enough inputs are provided. Fee to pay:", httpRequestException.Message);

				roundConfig.Denomination = new Money(0.008m, MoneyUnit.BTC); // one satoshi less than our output
				roundConfig.ConnectionConfirmationTimeout = 2;
				coordinator.UpdateRoundConfig(roundConfig);
				coordinator.FailAllRoundsInInputRegistration();
				InputsResponse inputsResponse = await aliceClient.PostInputsAsync(inputsRequest);
				Assert.NotNull(inputsResponse.BlindedOutputSignature);
				Assert.NotEqual(Guid.Empty, inputsResponse.UniqueId);
				Assert.True(inputsResponse.RoundId > 0);
				long roundId = inputsResponse.RoundId;

				Assert.Null(await aliceClient.PostConfirmationAsync(inputsResponse.RoundId, inputsResponse.UniqueId));

				CcjRunningRoundState roundState = await satoshiClient.GetRoundStateAsync(roundId);
				Assert.Equal(CcjRoundPhase.InputRegistration, roundState.Phase);
				Assert.Equal(1, roundState.RegisteredPeerCount);

				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostInputsAsync(inputsRequest));
				Assert.Equal($"{HttpStatusCode.BadRequest.ToReasonString()}\nBlinded output has already been registered.", httpRequestException.Message);

				roundState = await satoshiClient.GetRoundStateAsync(roundId);
				Assert.Equal(CcjRoundPhase.InputRegistration, roundState.Phase);
				Assert.Equal(1, roundState.RegisteredPeerCount);

				inputsRequest.BlindedOutputScriptHex = "foo";
				proof = key.SignMessage(inputsRequest.BlindedOutputScriptHex);
				inputsRequest.Inputs.First().Proof = proof;
				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostInputsAsync(inputsRequest));
				Assert.Equal($"{HttpStatusCode.BadRequest.ToReasonString()}\nInvalid blinded output hex.", httpRequestException.Message);

				roundState = await satoshiClient.GetRoundStateAsync(roundId);
				Assert.Equal(CcjRoundPhase.InputRegistration, roundState.Phase);
				Assert.Equal(1, roundState.RegisteredPeerCount);

				var blindingKey = Global.Coordinator.RsaKey;
				byte[] scriptBytes = key.ScriptPubKey.ToBytes();
				var (BlindingFactor, BlindedData) = blindingKey.PubKey.Blind(scriptBytes);
				inputsRequest.BlindedOutputScriptHex = ByteHelpers.ToHex(BlindedData);
				proof = key.SignMessage(inputsRequest.BlindedOutputScriptHex);
				inputsRequest.Inputs.First().Proof = proof;
				inputsResponse = await aliceClient.PostInputsAsync(inputsRequest);
				Assert.NotNull(inputsResponse.BlindedOutputSignature);
				Assert.NotEqual(Guid.Empty, inputsResponse.UniqueId);
				Assert.True(inputsResponse.RoundId > 0);

				roundState = await satoshiClient.GetRoundStateAsync(roundId);
				Assert.Equal(CcjRoundPhase.InputRegistration, roundState.Phase);
				Assert.Equal(1, roundState.RegisteredPeerCount);
				

				inputsRequest.BlindedOutputScriptHex = new Transaction().ToHex();
				proof = key.SignMessage(inputsRequest.BlindedOutputScriptHex);
				inputsRequest.Inputs.First().Proof = proof;
				inputsRequest.Inputs = new List<InputProofModel> { inputsRequest.Inputs.First(), inputsRequest.Inputs.First() };
				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostInputsAsync(inputsRequest));
				Assert.Equal($"{HttpStatusCode.BadRequest.ToReasonString()}\nCannot register an input twice.", httpRequestException.Message);

				var inputProofs = new List<InputProofModel>();
				for (int j = 0; j < 8; j++)
				{
					key = new Key();
					witnessAddress = key.PubKey.GetSegwitAddress(network);
					hash = await rpc.SendToAddressAsync(witnessAddress, new Money(0.01m, MoneyUnit.BTC));
					await rpc.GenerateAsync(1);
					tx = await rpc.GetRawTransactionAsync(hash);
					index = tx.Outputs.GetIndex(witnessAddress.ScriptPubKey);
					proof = key.SignMessage(inputsRequest.BlindedOutputScriptHex);
					inputProofs.Add(new InputProofModel { Input = new OutPoint(hash, index), Proof = proof });
				}
				await rpc.GenerateAsync(1);

				inputsRequest.Inputs = inputProofs;
				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostInputsAsync(inputsRequest));
				Assert.Equal($"{HttpStatusCode.BadRequest.ToReasonString()}\nMaximum 7 inputs can be registered.", httpRequestException.Message);

				inputProofs.RemoveLast();
				inputsRequest.Inputs = inputProofs;

				inputsResponse = await aliceClient.PostInputsAsync(inputsRequest);
				Assert.NotNull(inputsResponse.BlindedOutputSignature);
				Assert.NotEqual(Guid.Empty, inputsResponse.UniqueId);
				Assert.True(inputsResponse.RoundId > 0);
				roundId = inputsResponse.RoundId;

				await Task.Delay(50);
				roundState = await satoshiClient.GetRoundStateAsync(roundId);
				Assert.Equal(CcjRoundPhase.ConnectionConfirmation, roundState.Phase);
				Assert.Equal(2, roundState.RegisteredPeerCount);
				var inputRegistrableRoundState = await satoshiClient.GetRegistrableRoundStateAsync();
				Assert.Equal(0, inputRegistrableRoundState.RegisteredPeerCount);

				roundConfig.ConnectionConfirmationTimeout = 1; // One second.
				coordinator.UpdateRoundConfig(roundConfig);
				coordinator.FailAllRoundsInInputRegistration();

				roundState = await satoshiClient.GetRoundStateAsync(roundId);
				Assert.Equal(CcjRoundPhase.ConnectionConfirmation, roundState.Phase);
				Assert.Equal(2, roundState.RegisteredPeerCount);

				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostInputsAsync(inputsRequest));
				Assert.Equal($"{HttpStatusCode.BadRequest.ToReasonString()}\nInput is already registered in another round.", httpRequestException.Message);

				// Wait until input registration times out.
				await Task.Delay(3000);
				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostInputsAsync(inputsRequest));
				Assert.StartsWith($"{HttpStatusCode.BadRequest.ToReasonString()}\nInput is banned from participation for", httpRequestException.Message);

				states = await satoshiClient.GetAllRoundStatesAsync();
				foreach (var rs in states.Where(x => x.Phase == CcjRoundPhase.InputRegistration))
				{
					Assert.Equal(0, rs.RegisteredPeerCount);
				}

				#endregion

				#region PostConfirmationPostUnconfirmation
				// <-------------------------->
				// POST CONFIRMATION and POST UNCONFIRMATION tests
				// <-------------------------->

				key = new Key();
				witnessAddress = key.PubKey.GetSegwitAddress(network);
				hash = await rpc.SendToAddressAsync(witnessAddress, new Money(0.01m, MoneyUnit.BTC));
				await rpc.GenerateAsync(1);
				tx = await rpc.GetRawTransactionAsync(hash);
				index = tx.Outputs.GetIndex(witnessAddress.ScriptPubKey);
				scriptBytes = new Key().PubKey.GetSegwitAddress(network).ScriptPubKey.ToBytes();
				(BlindingFactor, BlindedData) = blindingKey.PubKey.Blind(scriptBytes);

				inputsResponse = await aliceClient.PostInputsAsync(new Key().ScriptPubKey, BlindedData, new InputProofModel { Input = new OutPoint(hash, index), Proof = key.SignMessage(ByteHelpers.ToHex(BlindedData)) });

				Assert.NotNull(inputsResponse.BlindedOutputSignature);
				Assert.NotEqual(Guid.Empty, inputsResponse.UniqueId);
				Guid uniqueAliceId = inputsResponse.UniqueId;
				Assert.True(inputsResponse.RoundId > 0);
				roundId = inputsResponse.RoundId;
				Assert.Null(await aliceClient.PostConfirmationAsync(roundId, uniqueAliceId));
				// Double the request.
				Assert.Null(await aliceClient.PostConfirmationAsync(roundId, uniqueAliceId));
				// badrequests
				using (var response = await torClient.SendAsync(HttpMethod.Post, $"/api/v1/btc/chaumiancoinjoin/confirmation"))
				{
					Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
					Assert.Null(await response.Content.ReadAsJsonAsync<string>());
				}
				using (var response = await torClient.SendAsync(HttpMethod.Post, $"/api/v1/btc/chaumiancoinjoin/confirmation?uniqueId={uniqueAliceId}"))
				{
					Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
					Assert.Null(await response.Content.ReadAsJsonAsync<string>());
				}
				using (var response = await torClient.SendAsync(HttpMethod.Post, $"/api/v1/btc/chaumiancoinjoin/confirmation?roundId={roundId}"))
				{
					Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
					Assert.Equal("Invalid uniqueId provided.", await response.Content.ReadAsJsonAsync<string>());
				}
				using (var response = await torClient.SendAsync(HttpMethod.Post, $"/api/v1/btc/chaumiancoinjoin/confirmation?uniqueId=foo&roundId={roundId}"))
				{
					Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
					Assert.Equal("Invalid uniqueId provided.", await response.Content.ReadAsJsonAsync<string>());
				}
				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostConfirmationAsync(roundId, Guid.Empty));
				Assert.Equal($"{HttpStatusCode.BadRequest.ToReasonString()}\nInvalid uniqueId provided.", httpRequestException.Message);
				using (var response = await torClient.SendAsync(HttpMethod.Post, $"/api/v1/btc/chaumiancoinjoin/confirmation?uniqueId={uniqueAliceId}&roundId=bar"))
				{
					Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
					Assert.Null(await response.Content.ReadAsJsonAsync<string>());
				}
				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostConfirmationAsync(0, uniqueAliceId));
				Assert.Equal(HttpStatusCode.BadRequest.ToReasonString(), httpRequestException.Message);
				Assert.Null(await aliceClient.PostConfirmationAsync(roundId, uniqueAliceId));
				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostConfirmationAsync(long.MaxValue, uniqueAliceId));
				Assert.Equal($"{HttpStatusCode.NotFound.ToReasonString()}\nRound not found.", httpRequestException.Message);
				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostConfirmationAsync(roundId, Guid.NewGuid()));
				Assert.Equal($"{HttpStatusCode.NotFound.ToReasonString()}\nAlice not found.", httpRequestException.Message);

				roundConfig.ConnectionConfirmationTimeout = 60;
				coordinator.UpdateRoundConfig(roundConfig);
				coordinator.FailAllRoundsInInputRegistration();
				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostConfirmationAsync(roundId, uniqueAliceId));
				Assert.Equal($"{HttpStatusCode.Gone.ToReasonString()}\nRound is not running.", httpRequestException.Message);

				inputsResponse = await aliceClient.PostInputsAsync(new Key().ScriptPubKey, BlindedData, new InputProofModel { Input = new OutPoint(hash, index), Proof = key.SignMessage(ByteHelpers.ToHex(BlindedData)) });
				Assert.NotNull(inputsResponse.BlindedOutputSignature);
				Assert.NotEqual(Guid.Empty, inputsResponse.UniqueId);
				uniqueAliceId = inputsResponse.UniqueId;
				Assert.True(inputsResponse.RoundId > 0);
				roundId = inputsResponse.RoundId;

				Assert.Null(await aliceClient.PostConfirmationAsync(roundId, uniqueAliceId));
				await aliceClient.PostUnConfirmationAsync(roundId, uniqueAliceId);
				using (var response = await torClient.SendAsync(HttpMethod.Post, $"/api/v1/btc/chaumiancoinjoin/unconfirmation?uniqueId={uniqueAliceId}&roundId={roundId}"))
				{
					Assert.True(response.IsSuccessStatusCode);
					Assert.Equal(HttpStatusCode.OK, response.StatusCode);
					Assert.Equal("Alice not found.", await response.Content.ReadAsJsonAsync<string>());
				}
				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostConfirmationAsync(roundId, uniqueAliceId));
				Assert.Equal($"{HttpStatusCode.NotFound.ToReasonString()}\nAlice not found.", httpRequestException.Message);

				#endregion

				#region PostOutput
				// <-------------------------->
				// POST OUTPUT tests
				// <-------------------------->

				var key1 = new Key();
				var key2 = new Key();
				var outputAddress1 = key1.PubKey.GetSegwitAddress(network);
				var outputAddress2 = key2.PubKey.GetSegwitAddress(network);
				var hash1 = await rpc.SendToAddressAsync(outputAddress1, new Money(0.01m, MoneyUnit.BTC));
				var hash2 = await rpc.SendToAddressAsync(outputAddress2, new Money(0.01m, MoneyUnit.BTC));
				await rpc.GenerateAsync(1);
				var tx1 = await rpc.GetRawTransactionAsync(hash1);
				var tx2 = await rpc.GetRawTransactionAsync(hash2);
				var index1 = 0;
				for (int i = 0; i < tx1.Outputs.Count; i++)
				{
					var output = tx1.Outputs[i];
					if (output.ScriptPubKey == outputAddress1.ScriptPubKey)
					{
						index1 = i;
					}
				}
				var index2 = 0;
				for (int i = 0; i < tx2.Outputs.Count; i++)
				{
					var output = tx2.Outputs[i];
					if (output.ScriptPubKey == outputAddress2.ScriptPubKey)
					{
						index2 = i;
					}
				}

				var blinded1 = blindingKey.PubKey.Blind(outputAddress1.ScriptPubKey.ToBytes());
				var blinded2 = blindingKey.PubKey.Blind(outputAddress2.ScriptPubKey.ToBytes());

				string blindedOutputScriptHex1 = ByteHelpers.ToHex(blinded1.BlindedData);
				string blindedOutputScriptHex2 = ByteHelpers.ToHex(blinded2.BlindedData);

				var input1 = new OutPoint(hash1, index1);
				var input2 = new OutPoint(hash2, index2);

				inputsResponse = await aliceClient.PostInputsAsync(new Key().ScriptPubKey, blinded1.BlindedData, new InputProofModel { Input = input1, Proof = key1.SignMessage(blindedOutputScriptHex1) });
				Guid uniqueAliceId1 = inputsResponse.UniqueId;
				roundId = inputsResponse.RoundId;
				byte[] unblindedSignature1 = blindingKey.PubKey.UnblindSignature(inputsResponse.BlindedOutputSignature, blinded1.BlindingFactor);

				inputsResponse = await aliceClient.PostInputsAsync(new Key().ScriptPubKey, blinded2.BlindedData, new InputProofModel { Input = input2, Proof = key2.SignMessage(blindedOutputScriptHex2) });
				Guid uniqueAliceId2 = inputsResponse.UniqueId;
				Assert.Equal(roundId, inputsResponse.RoundId);
				byte[] unblindedSignature2 = blindingKey.PubKey.UnblindSignature(inputsResponse.BlindedOutputSignature, blinded2.BlindingFactor);

				var roundHash = await aliceClient.PostConfirmationAsync(roundId, uniqueAliceId1);
				Assert.Equal(roundHash, await aliceClient.PostConfirmationAsync(roundId, uniqueAliceId1)); // Make sure it won't throw error for double confirming.
				Assert.Equal(roundHash, await aliceClient.PostConfirmationAsync(roundId, uniqueAliceId2));
				httpRequestException = await Assert.ThrowsAsync<HttpRequestException>(async () => await aliceClient.PostConfirmationAsync(roundId, uniqueAliceId2));
				Assert.Equal($"{HttpStatusCode.Gone.ToReasonString()}\nParticipation can be only confirmed from InputRegistration or ConnectionConfirmation phase. Current phase: OutputRegistration.", httpRequestException.Message);

				roundState = await satoshiClient.GetRoundStateAsync(roundId);
				Assert.Equal(CcjRoundPhase.OutputRegistration, roundState.Phase);

				await bobClient.PostOutputAsync(roundHash, outputAddress1.ScriptPubKey, unblindedSignature1);
				await bobClient.PostOutputAsync(roundHash, outputAddress2.ScriptPubKey, unblindedSignature2);

				roundState = await satoshiClient.GetRoundStateAsync(roundId);
				Assert.Equal(CcjRoundPhase.Signing, roundState.Phase);
				Assert.Equal(2, roundState.RegisteredPeerCount);
				Assert.Equal(2, roundState.RequiredPeerCount);

				#endregion

				#region GetCoinjoin
				// <-------------------------->
				// GET COINJOIN tests
				// <-------------------------->

				Transaction unsignedCoinJoin = await aliceClient.GetUnsignedCoinJoinAsync(roundId, uniqueAliceId1);
				Assert.Equal(unsignedCoinJoin.ToHex(), (await aliceClient.GetUnsignedCoinJoinAsync(roundId, uniqueAliceId1)).ToHex());
				Assert.Equal(unsignedCoinJoin.ToHex(), (await aliceClient.GetUnsignedCoinJoinAsync(roundId, uniqueAliceId2)).ToHex());

				Assert.Contains(outputAddress1.ScriptPubKey, unsignedCoinJoin.Outputs.Select(x => x.ScriptPubKey));
				Assert.Contains(outputAddress2.ScriptPubKey, unsignedCoinJoin.Outputs.Select(x => x.ScriptPubKey));
				Assert.True(2 == unsignedCoinJoin.Outputs.Count); // Because the two input is equal, so change addresses won't be used, nor coordinator fee will be taken.
				Assert.Contains(input1, unsignedCoinJoin.Inputs.Select(x => x.PrevOut));
				Assert.Contains(input2, unsignedCoinJoin.Inputs.Select(x => x.PrevOut));
				Assert.True(2 == unsignedCoinJoin.Inputs.Count);

				#endregion

				#region PostSignatures
				// <-------------------------->
				// POST SIGNATURES tests
				// <-------------------------->

				var partSignedCj1 = new Transaction(unsignedCoinJoin.ToHex());
				var partSignedCj2 = new Transaction(unsignedCoinJoin.ToHex());
				new TransactionBuilder()
							.AddKeys(key1)
							.AddCoins(new Coin(tx1, input1.N))
							.SignTransactionInPlace(partSignedCj1, SigHash.All);
				new TransactionBuilder()
							.AddKeys(key2)
							.AddCoins(new Coin(tx2, input2.N))
							.SignTransactionInPlace(partSignedCj2, SigHash.All);

				var myDic1 = new Dictionary<int, WitScript>();
				var myDic2 = new Dictionary<int, WitScript>();

				for (int i = 0; i < unsignedCoinJoin.Inputs.Count; i++)
				{
					var input = unsignedCoinJoin.Inputs[i];
					if (input.PrevOut == input1)
					{
						myDic1.Add(i, partSignedCj1.Inputs[i].WitScript);
					}
					if (input.PrevOut == input2)
					{
						myDic2.Add(i, partSignedCj2.Inputs[i].WitScript);
					}
				}

				await aliceClient.PostSignaturesAsync(roundId, uniqueAliceId1, myDic1);
				await aliceClient.PostSignaturesAsync(roundId, uniqueAliceId2, myDic2);

				uint256[] mempooltxs = await rpc.GetRawMempoolAsync();
				Assert.Contains(unsignedCoinJoin.GetHash(), mempooltxs);

				#endregion
			}
		}

		[Fact]
		public async Task Ccj100ParticipantsTestsAsync() 
		{
			var blindingKey = Global.Coordinator.RsaKey;
			var rpc = Global.RpcClient;
			var network = Global.RpcClient.Network;
			var coordinator = Global.Coordinator;
			Money denomination = new Money(0.1m, MoneyUnit.BTC);
			decimal coordinatorFeePercent = 0.1m;
			int anonymitySet = 100;
			int connectionConfirmationTimeout = 120;
			var roundConfig = new CcjRoundConfig(denomination, 140, coordinatorFeePercent, anonymitySet, 240, connectionConfirmationTimeout, 50, 50, 1);
			coordinator.UpdateRoundConfig(roundConfig);
			coordinator.FailAllRoundsInInputRegistration();
			await rpc.GenerateAsync(100); // So to make sure we have enough money.

			using (var bobClient = new BobClient(new Uri(RegTestFixture.BackendEndPoint)))
			using (var aliceClient = new AliceClient(new Uri(RegTestFixture.BackendEndPoint)))
			{
				var fundingTxCount = 0;
				var inputRegistrationUsers = new List<((BigInteger blindingFactor, byte[] blindedData) blinded, Script activeOutputScript, Script changeOutputScript, IEnumerable<InputProofModel> inputProofModels, List<(Key key, BitcoinWitPubKeyAddress address, uint256 txHash, Transaction tx, OutPoint input)> userInputData)>();
				for (int i = 0; i < roundConfig.AnonymitySet; i++)
				{
					var userInputData = new List<(Key key, BitcoinWitPubKeyAddress inputAddress, uint256 txHash, Transaction tx, OutPoint input)>();
					var activeOutputScript = new Key().ScriptPubKey;
					var changeOutputScript = new Key().ScriptPubKey;
					var blinded = blindingKey.PubKey.Blind(activeOutputScript.ToBytes());

					var inputProofModels = new List<InputProofModel>();
					int numberOfInputs = new Random().Next(1, 7);
					var receiveSatoshiSum = 0;
					for (int j = 0; j < numberOfInputs; j++)
					{
						var key = new Key();
						var receiveSatoshi = new Random().Next(1000, 100000000);
						receiveSatoshiSum += receiveSatoshi;
						if (j == numberOfInputs - 1)
						{
							receiveSatoshi = 100000000;
						}
						BitcoinWitPubKeyAddress inputAddress = key.PubKey.GetSegwitAddress(network);
						uint256 txHash = await rpc.SendToAddressAsync(inputAddress, receiveSatoshi);
						fundingTxCount++;
						Assert.NotNull(txHash);
						Transaction transaction = await rpc.GetRawTransactionAsync(txHash);

						var outputIndex = transaction.Outputs.GetIndex(inputAddress.ScriptPubKey);

						OutPoint input = new OutPoint(txHash, outputIndex);
						var inputProof = new InputProofModel { Input = input, Proof = key.SignMessage(ByteHelpers.ToHex(blinded.BlindedData)) };
						inputProofModels.Add(inputProof);
						
						GetTxOutResponse getTxOutResponse = await rpc.GetTxOutAsync(input.Hash, (int)input.N, includeMempool: true);
						// Check if inputs are unspent.	
						Assert.NotNull(getTxOutResponse);

						userInputData.Add((key, inputAddress, txHash, transaction, input));
					}

					inputRegistrationUsers.Add((blinded, activeOutputScript, changeOutputScript, inputProofModels, userInputData));
				}

				var mempool = await rpc.GetRawMempoolAsync();
				Assert.Equal(inputRegistrationUsers.SelectMany(x => x.userInputData).Count(), mempool.Count());
				
				while ((await rpc.GetRawMempoolAsync()).Length != 0)
				{
					await rpc.GenerateAsync(1);
				}


				Logger.TurnOff();
				long roundId = 0;

				var inputsRequests = new List<Task<InputsResponse>>();

				foreach (var user in inputRegistrationUsers)
				{
					inputsRequests.Add(aliceClient.PostInputsAsync(user.changeOutputScript, user.blinded.blindedData, user.inputProofModels.ToArray()));
				}

				var users = new List<((BigInteger blindingFactor, byte[] blindedData) blinded, Script activeOutputScript, Script changeOutputScript, IEnumerable<InputProofModel> inputProofModels, List<(Key key, BitcoinWitPubKeyAddress address, uint256 txHash, Transaction tx, OutPoint input)> userInputData, Guid uniqueId, byte[] unblindedSignature)>();
				for (int i = 0; i < inputRegistrationUsers.Count; i++)
				{
					var user = inputRegistrationUsers[i];
					var request = inputsRequests[i];

					var response = await request;
					if (roundId == 0)
					{
						roundId = response.RoundId;
					}
					else
					{
						Assert.Equal(roundId, response.RoundId);
					}
					// Because it's valuetuple.
					users.Add((user.blinded, user.activeOutputScript, user.changeOutputScript, user.inputProofModels, user.userInputData, response.UniqueId, blindingKey.PubKey.UnblindSignature(response.BlindedOutputSignature, user.blinded.blindingFactor)));
				}

				Logger.TurnOn();

				Assert.Equal(users.Count(), roundConfig.AnonymitySet);

				var confirmationRequests = new List<Task<string>>();

				foreach (var user in users)
				{
					confirmationRequests.Add(aliceClient.PostConfirmationAsync(roundId, user.uniqueId));
				}

				string roundHash = null;
				foreach(var request in confirmationRequests)
				{
					if(roundHash == null)
					{
						roundHash = await request;
					}
					else
					{
						Assert.Equal(roundHash, await request);
					}
				}

				var outputRequests = new List<Task>();
				foreach (var user in users)
				{
					outputRequests.Add(bobClient.PostOutputAsync(roundHash, user.activeOutputScript, user.unblindedSignature));
				}

				await Task.WhenAll(outputRequests);

				var coinjoinRequests = new List<Task<Transaction>>();
				foreach (var user in users)
				{
					coinjoinRequests.Add(aliceClient.GetUnsignedCoinJoinAsync(roundId, user.uniqueId));
				}

				Transaction unsignedCoinJoin = null;
				foreach (var request in coinjoinRequests)
				{
					if (unsignedCoinJoin == null)
					{
						unsignedCoinJoin = await request;
					}
					else
					{
						Assert.Equal(unsignedCoinJoin.ToHex(), (await request).ToHex());
					}
				}

				var signatureRequests = new List<Task>();
				foreach (var user in users)
				{
					var partSignedCj = new Transaction(unsignedCoinJoin.ToHex());
					new TransactionBuilder()
								.AddKeys(user.userInputData.Select(x=>x.key).ToArray())
								.AddCoins(user.userInputData.Select(x=> new Coin(x.tx, x.input.N)))
								.SignTransactionInPlace(partSignedCj, SigHash.All);

					var myDic = new Dictionary<int, WitScript>();

					for (int i = 0; i < unsignedCoinJoin.Inputs.Count; i++)
					{
						var input = unsignedCoinJoin.Inputs[i];
						if (user.userInputData.Select(x=>x.input).Contains(input.PrevOut))
						{
							myDic.Add(i, partSignedCj.Inputs[i].WitScript);
						}
					}

					signatureRequests.Add(aliceClient.PostSignaturesAsync(roundId, user.uniqueId, myDic));
				}

				await Task.WhenAll(signatureRequests);

				uint256[] mempooltxs = await rpc.GetRawMempoolAsync();
				Assert.Contains(unsignedCoinJoin.GetHash(), mempooltxs);

				var coins = new List<Coin>();
				var finalCoinjoin = await rpc.GetRawTransactionAsync(mempooltxs.First());
				foreach (var input in finalCoinjoin.Inputs)
				{
					var getTxOut = await rpc.GetTxOutAsync(input.PrevOut.Hash, (int)input.PrevOut.N, includeMempool: false);

					coins.Add(new Coin(input.PrevOut.Hash, input.PrevOut.N, getTxOut.TxOut.Value, getTxOut.TxOut.ScriptPubKey));
				}

				FeeRate feeRateTx = finalCoinjoin.GetFeeRate(coins.ToArray());
				var esr = await rpc.EstimateSmartFeeAsync((int)roundConfig.ConfirmationTarget, EstimateSmartFeeMode.Conservative, simulateIfRegTest: true);
				FeeRate feeRateReal = esr.FeeRate;

				Assert.True(feeRateReal.FeePerK - (feeRateReal.FeePerK/2) < feeRateTx.FeePerK); // Max 50% mistake.
				Assert.True(2 * feeRateReal.FeePerK > feeRateTx.FeePerK); // Max 200% mistake.

				var activeOutput = finalCoinjoin.GetIndistinguishableOutputs().OrderByDescending(x=>x.count).First();
				Assert.True(activeOutput.value >= roundConfig.Denomination);
				Assert.True(activeOutput.value >= roundConfig.AnonymitySet);
			}
		}

		[Fact]
		public async Task CcjClientTestsAsync()
		{
			string password = "password";
			var rpc = Global.RpcClient;
			var network = Global.RpcClient.Network;
			var coordinator = Global.Coordinator;
			Money denomination = new Money(0.1m, MoneyUnit.BTC);
			decimal coordinatorFeePercent = 0.1m;
			int anonymitySet = 2;
			int connectionConfirmationTimeout = 60;
			var roundConfig = new CcjRoundConfig(denomination, 140, coordinatorFeePercent, anonymitySet, 240, connectionConfirmationTimeout, 50, 50, 1);
			coordinator.UpdateRoundConfig(roundConfig);
			coordinator.FailAllRoundsInInputRegistration();
			await rpc.GenerateAsync(3); // So to make sure we have enough money.
			var keyManager = KeyManager.CreateNew(out _, password);
			var key1 = keyManager.GenerateNewKey("foo", KeyState.Clean, false);
			var key2 = keyManager.GenerateNewKey("bar", KeyState.Clean, false);
			var bech1 = key1.GetP2wpkhAddress(network);
			var bech2 = key2.GetP2wpkhAddress(network);
			var amount1 = new Money(0.03m, MoneyUnit.BTC);
			var amount2 = new Money(0.08m, MoneyUnit.BTC);
			var txid1 = await rpc.SendToAddressAsync(bech1, amount1, replaceable: false);
			var txid2 = await rpc.SendToAddressAsync(bech2, amount2, replaceable: false);
			key1.KeyState = KeyState.Used;
			key2.KeyState = KeyState.Used;
			var tx1 = await rpc.GetRawTransactionAsync(txid1);
			var tx2 = await rpc.GetRawTransactionAsync(txid2);
			await rpc.GenerateAsync(1);
			var height = await rpc.GetBlockCountAsync();
			var smartCoin1 = new SmartCoin(txid1, tx1.Outputs.GetIndex(bech1.ScriptPubKey), bech1.ScriptPubKey, amount1, tx1.Inputs.Select(x => new TxoRef(x.PrevOut)).ToArray(), height, rbf: false);
			var smartCoin2 = new SmartCoin(txid2, tx2.Outputs.GetIndex(bech2.ScriptPubKey), bech2.ScriptPubKey, amount2, tx2.Inputs.Select(x => new TxoRef(x.PrevOut)).ToArray(), height, rbf: false);

			var chaumianClient = new CcjClient(Global.RpcClient.Network, Global.Coordinator.RsaKey.PubKey, keyManager, new Uri(RegTestFixture.BackendEndPoint));
			try
			{
				chaumianClient.Start();

				Assert.Throws<NotSupportedException>(() => chaumianClient.QueueCoinsToMix(password, smartCoin1, smartCoin2));
				smartCoin1.Locked = true;
				smartCoin2.Locked = true;
				Assert.Throws<SecurityException>(() => chaumianClient.QueueCoinsToMix("asdasdasd", smartCoin1, smartCoin2));

				// Make sure it doesn't throw.
				await chaumianClient.DequeueCoinsFromMixAsync((smartCoin1.TransactionId, smartCoin1.Index));

				Assert.True(2 == chaumianClient.QueueCoinsToMix(password, smartCoin1, smartCoin2).Count());
				await chaumianClient.DequeueCoinsFromMixAsync((smartCoin1.TransactionId, 111));
				await chaumianClient.DequeueCoinsFromMixAsync(smartCoin1, smartCoin2);
				Assert.True(2 == chaumianClient.QueueCoinsToMix(password, smartCoin1, smartCoin2).Count());
				await chaumianClient.DequeueCoinsFromMixAsync(smartCoin1);
				await chaumianClient.DequeueCoinsFromMixAsync(smartCoin2);
			}
			finally
			{
				if (chaumianClient != null)
				{
					await chaumianClient.StopAsync();
				}
			}
		}

		#endregion
	}
}
