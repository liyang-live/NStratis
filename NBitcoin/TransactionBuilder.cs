﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace NBitcoin
{
	public interface ICoinSelector
	{
		IEnumerable<Coin> Select(IEnumerable<Coin> coins, Money target);
	}

	/// <summary>
	/// Algorithm implemented by bitcoin core https://github.com/bitcoin/bitcoin/blob/master/src/wallet.cpp#L1276
	/// Minimize the change
	/// </summary>
	public class DefaultCoinSelector : ICoinSelector
	{
		public DefaultCoinSelector()
		{

		}
		Random _Rand = new Random();
		public DefaultCoinSelector(int seed)
		{
			_Rand = new Random(seed);
		}
		#region ICoinSelector Members

		public IEnumerable<Coin> Select(IEnumerable<Coin> coins, Money target)
		{
			var targetCoin = coins
							.FirstOrDefault(c => c.TxOut.Value == target);
			//If any of your UTXO² matches the Target¹ it will be used.
			if(targetCoin != null)
				return new[] { targetCoin };

			var orderedCoins = coins.OrderBy(s => s.TxOut.Value).ToArray();
			List<Coin> result = new List<Coin>();
			Money total = Money.Zero;

			foreach(var coin in orderedCoins)
			{
				if(coin.TxOut.Value < target)
				{
					total += coin.TxOut.Value;
					result.Add(coin);
					//If the "sum of all your UTXO smaller than the Target" happens to match the Target, they will be used. (This is the case if you sweep a complete wallet.)
					if(total == target)
						return result;

				}
				else
				{
					if(total < target)
					{
						//If the "sum of all your UTXO smaller than the Target" doesn't surpass the target, the smallest UTXO greater than your Target will be used.
						return new[] { coin };
					}
					else
					{
						//						Else Bitcoin Core does 1000 rounds of randomly combining unspent transaction outputs until their sum is greater than or equal to the Target. If it happens to find an exact match, it stops early and uses that.
						//Otherwise it finally settles for the minimum of
						//the smallest UTXO greater than the Target
						//the smallest combination of UTXO it discovered in Step 4.
						var allCoins = orderedCoins.ToArray();
						Money minTotal = null;
						List<Coin> minSelection = null;
						for(int _ = 0 ; _ < 1000 ; _++)
						{
							var selection = new List<Coin>();
							Shuffle(allCoins, _Rand);
							total = Money.Zero;
							for(int i = 0 ; i < allCoins.Length ; i++)
							{
								selection.Add(allCoins[i]);
								total += allCoins[i].TxOut.Value;
								if(total == target)
									return selection;
								if(total > target)
									break;
							}
							if(total < target)
							{
								return null;
							}
							if(minTotal == null || total < minTotal)
							{
								minTotal = total;
								minSelection = selection;
							}
						}
					}
				}
			}
			return result;
		}

		internal static void Shuffle<T>(T[] list, Random random)
		{
			int n = list.Length;
			while(n > 1)
			{
				n--;
				int k = random.Next(n + 1);
				T value = list[k];
				list[k] = list[n];
				list[n] = value;
			}
		}
		internal static void Shuffle<T>(List<T> list, Random random)
		{
			int n = list.Count;
			while(n > 1)
			{
				n--;
				int k = random.Next(n + 1);
				T value = list[k];
				list[k] = list[n];
				list[n] = value;
			}
		}


		#endregion
	}

	public class NotEnoughFundsException : Exception
	{
		public NotEnoughFundsException()
		{
		}
		public NotEnoughFundsException(string message)
			: base(message)
		{
		}
		public NotEnoughFundsException(string message, Exception inner)
			: base(message, inner)
		{
		}
	}
	public class TransactionBuilder
	{
		public TransactionBuilder()
		{
			Fees = Money.Zero;
			_Rand = new Random();
			CoinSelector = new DefaultCoinSelector();
		}
		Random _Rand;
		public TransactionBuilder(int seed)
		{
			Fees = Money.Zero;
			_Rand = new Random(seed);
			CoinSelector = new DefaultCoinSelector(seed);
		}
		public Money Fees
		{
			get;
			set;
		}

		public ICoinSelector CoinSelector
		{
			get;
			set;
		}

		public Script ChangeScript
		{
			get;
			set;
		}
		List<Action<Transaction>> _Builders = new List<Action<Transaction>>();
		List<Coin> _Coins = new List<Coin>();
		public List<Coin> Coins
		{
			get
			{
				return _Coins;
			}
		}

		List<Key> _Keys = new List<Key>();

		public TransactionBuilder AddKeys(params Key[] keys)
		{
			_Keys.AddRange(keys);
			return this;
		}
		public TransactionBuilder AddCoins(params Coin[] coins)
		{
			Coins.AddRange(coins);
			return this;
		}
		public TransactionBuilder SendTo(BitcoinAddress destination, Money money)
		{
			return SendTo(destination.ID, money);
		}

		public TransactionBuilder SendTo(TxDestination id, Money money)
		{
			_Builders.Add(tx =>
			{
				tx.Outputs.Add(new TxOut(money, id.CreateScriptPubKey()));
			});
			return this;
		}

		public TransactionBuilder SetFees(Money fees)
		{
			if(fees == null)
				throw new ArgumentNullException("fees");
			Fees = fees;
			return this;
		}

		public TransactionBuilder SendChange(BitcoinAddress destination)
		{
			return SendChange(destination.ID);
		}

		private TransactionBuilder SendChange(TxDestination destination)
		{
			if(destination == null)
				throw new ArgumentNullException("destination");
			ChangeScript = destination.CreateScriptPubKey();
			return this;
		}
		public TransactionBuilder SetCoinSelector(ICoinSelector selector)
		{
			if(selector == null)
				throw new ArgumentNullException("selector");
			CoinSelector = selector;
			return this;
		}
		public Transaction BuildTransaction()
		{
			Transaction tx = new Transaction();
			foreach(var builder in _Builders)
				builder(tx);
			var target = tx.TotalOut + Fees;
			var selection = CoinSelector.Select(Coins, target);
			if(selection == null)
			{
				throw new NotEnoughFundsException("Not enough fund to cover the target");
			}
			var total = selection.Select(s => s.TxOut.Value).Sum();
			if(total != target)
			{
				if(ChangeScript == null)
					throw new InvalidOperationException("A change address should be specified");
				tx.Outputs.Add(new TxOut(total - target, ChangeScript));
			}
			DefaultCoinSelector.Shuffle(tx.Outputs, _Rand);
			foreach(var coin in selection)
			{
				tx.AddInput(new TxIn(coin.Outpoint));
			}
			int i = 0;
			foreach(var coin in selection)
			{
				Sign(tx, tx.Inputs[i], coin, i);
				i++;
			}
			return tx;
		}

		public bool Verify(Transaction tx)
		{
			for(int i = 0 ; i < tx.Inputs.Count ; i++)
			{
				var txIn = tx.Inputs[i];
				var coin = FindCoin(txIn.PrevOut);
				if(coin == null)
					throw new KeyNotFoundException("Impossible to find the scriptPubKey of outpoint " + txIn.PrevOut);
				if(!Script.VerifyScript(txIn.ScriptSig, coin.TxOut.ScriptPubKey, tx, i))
					return false;
			}
			return true;
		}

		private Coin FindCoin(OutPoint outPoint)
		{
			return _Coins.FirstOrDefault(c => c.Outpoint == outPoint);
		}

		readonly static PayToScriptHashTemplate payToScriptHash = new PayToScriptHashTemplate();
		readonly static PayToPubkeyHashTemplate payToPubKeyHash = new PayToPubkeyHashTemplate();
		readonly static PayToPubkeyTemplate payToPubKey = new PayToPubkeyTemplate();
		readonly static PayToMultiSigTemplate payToMultiSig = new PayToMultiSigTemplate();

		private void Sign(Transaction tx, TxIn input, Coin coin, int n)
		{
			if(payToScriptHash.CheckScriptPubKey(coin.TxOut.ScriptPubKey))
			{
				var scriptCoin = coin as ScriptCoin;
				if(scriptCoin == null)
					throw new InvalidOperationException("A coin with a P2SH scriptPubKey was detected, however this coin is not a ScriptCoin");
				var scriptSig = CreateScriptSig(tx, input, coin, n, scriptCoin.Redeem);
				var ops = scriptSig.ToOps().ToList();
				ops.Add(Op.GetPushOp(scriptCoin.Redeem.ToRawScript(true)));
				input.ScriptSig = new Script(ops.ToArray());
			}
			else
			{
				input.ScriptSig = CreateScriptSig(tx, input, coin, n, coin.TxOut.ScriptPubKey);
			}
		}

		private Script CreateScriptSig(Transaction tx, TxIn input, Coin coin, int n, Script scriptPubKey)
		{
			input.ScriptSig = scriptPubKey;

			var pubKeyHashParams = payToPubKeyHash.ExtractScriptPubKeyParameters(scriptPubKey);
			if(pubKeyHashParams != null)
			{
				var key = FindKey(pubKeyHashParams);
				AssetHasKey(key, coin);
				var hash = input.ScriptSig.SignatureHash(tx, n, SigHash.All);
				var sig = key.Sign(hash);
				return payToPubKeyHash.GenerateScriptSig(new TransactionSignature(sig, SigHash.All), key.PubKey);
			}

			var multiSigParams = payToMultiSig.ExtractScriptPubKeyParameters(scriptPubKey);
			if(multiSigParams != null)
			{
				var keys =
					multiSigParams
					.PubKeys
					.Select(p => FindKey(p))
					.Where(k => k != null)
					.Take(multiSigParams.SignatureCount)
					.ToArray();
				if(keys.Length != multiSigParams.SignatureCount)
					throw new KeyNotFoundException("Not enough key for multi sig of coin " + coin.Outpoint);
				return payToMultiSig.GenerateScriptSig(
					keys.Select(k =>
					{
						var hash = input.ScriptSig.SignatureHash(tx, n, SigHash.All);
						var sig = k.Sign(hash);
						return new TransactionSignature(sig, SigHash.All);
					}).ToArray());
			}

			var pubKeyParams = payToPubKey.ExtractScriptPubKeyParameters(scriptPubKey);
			if(pubKeyParams != null)
			{
				var key = FindKey(pubKeyParams);
				AssetHasKey(key, coin);
				var hash = input.ScriptSig.SignatureHash(tx, n, SigHash.All);
				var sig = key.Sign(hash);
				return payToPubKey.GenerateScriptSig(new TransactionSignature(sig, SigHash.All));
			}

			throw new NotSupportedException("Unsupported scriptPubKey");
		}

		private void AssetHasKey(Key key, Coin coin)
		{
			if(key == null)
				throw new KeyNotFoundException("Key not found for coin " + coin.Outpoint);
		}

		private Key FindKey(TxDestination id)
		{
			return _Keys.FirstOrDefault(k => k.PubKey.ID == id);
		}

		private Key FindKey(PubKey pubKeyParams)
		{
			return _Keys.FirstOrDefault(k => k.PubKey == pubKeyParams);
		}
	}
}