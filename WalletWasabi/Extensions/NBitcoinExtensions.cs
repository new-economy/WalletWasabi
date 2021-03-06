using WalletWasabi.Models;
using System.Collections.Generic;
using System;
using WalletWasabi.Helpers;
using System.Linq;

namespace NBitcoin
{
	public static class NBitcoinExtensions
	{
		public static TxoRef ToTxoRef(this OutPoint me) => new TxoRef(me);

		public static IEnumerable<TxoRef> ToTxoRefs(this TxInList me)
		{
			foreach (var input in me)
			{
				yield return input.PrevOut.ToTxoRef();
			}
		}

		public static int GetIndex(this TxOutList me, Script script)
		{
			var index = -1;
			for (int i = 0; i < me.Count; i++)
			{
				var output = me[i];
				if(output.ScriptPubKey == script)
				{
					index = i;
				}
			}

			if(index == -1)
			{
				throw new InvalidOperationException("Script not found.");
			}
			return index;
		}

		public static string ToHex(this IBitcoinSerializable me)
		{
			return ByteHelpers.ToHex(me.ToBytes());
		}

		public static void FromHex(this IBitcoinSerializable me, string hex)
		{
			Guard.NotNullOrEmptyOrWhitespace(nameof(hex), hex);
			me.FromBytes(ByteHelpers.FromHex(hex));
		}

		public static IEnumerable<(Money value, int count)> GetIndistinguishableOutputs(this Transaction me)
		{
			var ret = new List<(Money Value, int count)>();
			foreach (Money v in me.Outputs.Select(x => x.Value).Distinct())
			{
				ret.Add((v, me.Outputs.Count(x => x.Value == v)));
			}

			return ret;
		}
	}
}
