using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace EcsServerLibM
{


	public static class BitIdM
	{
		const int UINT_CNT_BIT = 32;
		const int HEX_STR_BYTE = 2;
		static public uint[] CreateNewBitId(ref int _iCurCntIdx)
		{
			var iCurCntIdx = Interlocked.Increment(ref _iCurCntIdx);

			var iCntArr = iCurCntIdx / UINT_CNT_BIT;
			var iLeft = iCurCntIdx % UINT_CNT_BIT;
			var iShift = iLeft - 1;

			if (iLeft != 0)
			{
				iCntArr++;
			}
			else
			{
				iShift = UINT_CNT_BIT - 1;     // 나머지가 0이면 최대 시프트
			}

			var newIndex = new uint[iCntArr];
			newIndex[iCntArr - 1] = ((uint)1 << iShift);
			return newIndex;
		}

		public static void PrintBitId(uint[] bitId)
		{
			Debug.WriteLine(ToStringBitId(bitId));
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="strLength"></param>
		/// <param name="iCntByte"></param>
		/// <returns></returns>
		static int GetCntNeedArrayWithStrByteCnt(int strLength, int iCntByte)
		{
			if (strLength <= 0)
			{
				return 0;
			}

			var iCntNeedArray = strLength / iCntByte;

			if (strLength % iCntByte != 0)
			{
				iCntNeedArray++;
			}
			return iCntNeedArray;
		}

		public static uint[] ToBitId(string strBitId)
		{
			var inputSpan = strBitId.AsSpan();

			var iCntArr = GetCntNeedArrayWithStrByteCnt(inputSpan.Length, HEX_STR_BYTE);
			var rtnArr = new uint[iCntArr];

			int startSlice = inputSpan.Length - HEX_STR_BYTE;
			ReadOnlySpan<char> chunkSpan;
			int i = 0;
			while (true)
			{
				if (startSlice < 0)
				{
					chunkSpan = inputSpan.Slice(0, startSlice + HEX_STR_BYTE);
					rtnArr[i++] = Convert.ToUInt32(chunkSpan.ToString(), 16);
					break;
				}
				else
				{
					chunkSpan = inputSpan.Slice(startSlice, HEX_STR_BYTE);
				}

				startSlice -= HEX_STR_BYTE;
				rtnArr[i++] = Convert.ToUInt32(chunkSpan.ToString(), 16);
			}

			return rtnArr;
		}

		public static string ToStringBitId(uint[] bitId)
		{
			StringBuilder sb = new StringBuilder();
			string bitStr;
			for (int i = bitId.Length - 1; i >= 0; i--)
			{
				if (bitId[i] == 0)
					bitStr = Convert.ToString(bitId[i], 16).PadLeft(2, '0');
				else
					bitStr = Convert.ToString(bitId[i], 16);

				sb.Append(bitStr);
			}

			return sb.ToString();
		}


		/// <summary>
		/// 해당 아이디가 들어있는지 검사하는 함수
		/// </summary>
		/// <param name="targetId"></param>
		/// <param name="compId"></param>
		/// <returns></returns>
		static public int ContainsId(uint[] targetId, uint[] compId)
		{
			if (targetId.Length < compId.Length)
				return 0;

			int iContainPart = 0;
			int iCntSamePart = 0;
			for (int i = 0; i < compId.Length; i++)
			{
				uint bitPart = targetId[i];
				uint bitPartDest = compId[i];

				if (bitPart == bitPartDest)
				{
					iCntSamePart++;
				}

				uint bitResult = bitPartDest & bitPart;
				if (bitPart == bitResult)   // 포함되지 않음
				{
					iContainPart++;
				}
			}

			if (targetId.Length == iCntSamePart)    // 완전 동일
			{
				return 1;   // 동일
			}
			else if (iContainPart > 0)
			{
				return 2;
			}

			return 0;   // 포함되지 않음
		}

		/// <summary>
		/// BitId 분해할 때(SplitBitIds) 발견된 array index와 그때 그 uint값을 넘겨주면 uint[] bitId를 만들어 주는 함수
		/// </summary>
		/// <param name="arrIdx"></param>
		/// <param name="val"></param>
		/// <returns></returns>
		static public uint[] GetBitIdWithArrIndex(int arrIdx, uint val)
		{
			var rtnBitId = new uint[arrIdx + 1];
			rtnBitId[arrIdx] = val;
			return rtnBitId;
		}

		/// <summary>
		/// 조합된 BitId들을 개별ID로 모두 분리하는 함수
		/// </summary>
		/// <param name="bitId"></param>
		/// <returns></returns>
		static public List<uint[]> SplitBitIds(uint[] bitId)
		{
			uint bit = 1;
			uint splitId = 0;
			List<uint[]> rtnIds = new List<uint[]>();
			for (int i = 0; i < bitId.Length; i++)
			{
				for (int k = 0; k < UINT_CNT_BIT; k++)
				{
					bit <<= k;
					splitId = bitId[i] & bit;
					if (splitId == bit)
					{
						rtnIds.Add(GetBitIdWithArrIndex(i, splitId));
					}
				}
				bit = 1;
			}
			return rtnIds;
		}

		/// <summary>
		/// 여러개의 Bit ID를 하나로 Joion
		/// </summary>
		/// <param name="bitIds"></param>
		/// <returns></returns>
		static public uint[] JoinBitIds(IEnumerable<uint[]> bitIds)
		{
			uint[] joinBitId = new uint[0];
			foreach (var bitId in bitIds)
			{
				joinBitId = BitIdM.Add(joinBitId, bitId);
			}

			return joinBitId;
		}

		/// <summary>
		/// 분해된 BitId 리스트를 모두 strId 리스트로 변환하는 함수
		/// </summary>
		/// <param name="bitIds"></param>
		/// <returns></returns>
		static public List<string> ToStrBitIdsList(IEnumerable<uint[]> bitIds)
		{
			var strIdsList = new List<string>();
			foreach (var bitId in bitIds)
			{
				strIdsList.Add(ToStringBitId(bitId));
			}

			return strIdsList;
		}

		static public uint[] Add(uint[] targetId, uint[] addId)
		{
			if (targetId == null)
				targetId = new uint[0];

			var aLen = targetId.Length;
			var bLen = addId.Length;
			int iLenBig;
			int iLenSmall;

			uint[] bigLenId;
			uint[] smallLenId;


			if (aLen > bLen)
			{
				iLenBig = aLen;
				bigLenId = targetId;

				iLenSmall = bLen;
				smallLenId = targetId;
			}
			else
			{
				iLenBig = bLen;
				bigLenId = addId;

				iLenSmall = aLen;
				smallLenId = targetId;
			}

			uint[] rtn = new uint[iLenBig];

			for (int i = 0; i < iLenBig; i++)
			{
				if (i < iLenSmall)
				{
					rtn[i] = bigLenId[i] | smallLenId[i];
				}
				else
				{
					rtn[i] = bigLenId[i];
				}
			}

			return rtn;
		}


		static public uint[] InterSect(uint[] aId, uint[] bId)
		{
			var aLen = aId.Length;
			var bLen = bId.Length;
			int iLenBig = 0;
			int iLenSmall = 0;

			uint[] bigLenId;
			uint[] smallLenId;


			if (aLen > bLen)
			{
				iLenBig = aLen;
				bigLenId = aId;

				iLenSmall = bLen;
				smallLenId = aId;
			}
			else
			{
				iLenBig = bLen;
				bigLenId = bId;

				iLenSmall = aLen;
				smallLenId = aId;
			}

			uint[] rtn = new uint[iLenBig];

			for (int i = 0; i < iLenBig; i++)
			{
				if (i < iLenSmall)
				{
					rtn[i] = bigLenId[i] & smallLenId[i];
				}
				else
				{
					rtn[i] = 0;
				}
			}

			return rtn;
		}

		static public uint[] Sub(uint[] targetId, uint[] subId)
		{
			var aLen = targetId.Length;
			var bLen = subId.Length;
			int iLenBig;
			int iLenSmall;

			uint[] bigLenId;
			uint[] smallLenId;

			uint[] rtn;

			if (aLen > bLen)
			{
				iLenBig = aLen;
				bigLenId = targetId;

				iLenSmall = bLen;
				smallLenId = targetId;

				rtn = new uint[iLenBig];

				for (int i = 0; i < iLenBig; i++)
				{
					if (i < iLenSmall)
					{
						rtn[i] = bigLenId[i] & (~smallLenId[i]);
					}
					else
					{
						rtn[i] = bigLenId[i];
					}
				}
			}
			else
			{
				iLenBig = bLen;
				bigLenId = subId;

				iLenSmall = aLen;
				smallLenId = targetId;

				rtn = new uint[iLenBig];

				for (int i = 0; i < iLenBig; i++)
				{
					if (i < iLenSmall)
					{
						rtn[i] = smallLenId[i] & (~bigLenId[i]);
					}
					else
					{
						rtn[i] = 0;
					}
				}
			}

			return rtn;
		}

	}
}