using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace EcsServerLibM
{
	// 이상치 제거 (Outlier Detection): 갑작스런 큰 값들은 네트워크 불가지역에서의 결과일 가능성이 높기 때문에 제거합니다.
	// 이를 위해 IQR(Interquartile Range) 방법을 사용함.
	// 가중 이동 평균 (Weighted Moving Average): 최근 데이터에 더 많은 가중치를 부여하여 네트워크 딜레이 값을 추정합니다.
	public class InterQuartileM<T> where T : struct, IComparable<T>, IConvertible
	{
		//static public void RemoveOutliersForSpeed(ConcurrentQueue<T> data, List<T> result)
		//{
		//	if (data.Count < 4)
		//	{
		//		data.ToList();
		//		return ;  // 데이터가 너무 적으면 이상치 제거하지 않음
		//	}

		//	var listData = new List<T>(data);
		//	var count = listData.Count;

		//	listData.Sort();

		//	int quartileSize = count / 4;
		//	var q1 = listData[quartileSize];
		//	var q3 = listData[3 * quartileSize];
		//	var iqr = Convert.ToDouble(q3) - Convert.ToDouble(q1);
		//	var iqr15 = 1.5 * iqr;
		//	var lowerBound = Convert.ToDouble(q1) - iqr15;
		//	var upperBound = Convert.ToDouble(q3) + iqr15;

		//	listData.Where(x => Convert.ToDouble(x) >= lowerBound && Convert.ToDouble(x) <= upperBound).ToList();
		//	return;
		//}


		/// <summary>
		/// 이상지 제거 후 평균을 구합니다.
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="data"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static public long RemoveOutliersAndAverage<T>(T[] sortedArray, int arrCnt)
where T : IComparable<T>, IConvertible
		{
			if (arrCnt == 0) return 0; // 데이터가 없으면 0 리턴

			try
			{
				double sum = 0;
				if (arrCnt < 4)
				{					
					for(int i = 0; i < arrCnt; i++)
					{
						sum = sum + sortedArray[i].ToDouble(null);
					}
					return (long)(sum / arrCnt); // 데이터가 4개 미만이면 평균 계산 후 리턴
				}				

				// 사분위수 계산 (비트 시프트 최적화)
				int q1Idx = arrCnt >> 2;
				int q3Idx = q1Idx * 3;
				double q1 = sortedArray[q1Idx].ToDouble(null);
				double q3 = sortedArray[q3Idx].ToDouble(null);
				double iqr = q3 - q1;
				double iqr15 = iqr * 1.5;
				double lowerBound = q1 - iqr15;
				double upperBound = q3 + iqr15;

				// 정렬된 배열에서 유효 범위의 시작과 끝 인덱스 찾기
				int startIdx = 0;
				int endIdx = arrCnt - 1;

				// lowerBound보다 큰 첫 번째 인덱스 찾기
				while (startIdx < arrCnt && sortedArray[startIdx].ToDouble(null) < lowerBound)
					startIdx++;

				// upperBound보다 작거나 같은 마지막 인덱스 찾기  
				while (endIdx >= 0 && sortedArray[endIdx].ToDouble(null) > upperBound)
					endIdx--;

				// 유효한 범위의 합계 계산
				int validCount = endIdx - startIdx + 1;

				if (validCount > 0)
				{
					for (int i = startIdx; i <= endIdx; i++)
					{
						sum += sortedArray[i].ToDouble(null);
					}
					return (long)(sum / validCount);
				}

				return 0;
			}
			finally
			{
				
			}
		}

		/// <summary>
		/// 이상치 제거를 보다 정확하게 할 때
		/// </summary>
		/// <param name="data"></param>
		/// <returns></returns>
		static public List<T> RemoveOutliers(List<T> data)  // 
		{
			if (data.Count < 4) return data; // 충분한 데이터가 없으면 원본 데이터 반환

			// 사분위수 계산
			int n = data.Count;
			double q1 = GetQuantile(data, 0.25);
			double q3 = GetQuantile(data, 0.75);
			double iqr = q3 - q1;

			// IQR을 사용하여 이상치 정의
			double lowerBound = q1 - 1.5 * iqr;
			double upperBound = q3 + 1.5 * iqr;

			// 이상치 제거
			return data.Where(val => Convert.ToDouble(val) >= lowerBound && Convert.ToDouble(val) <= upperBound).ToList();
		}

		// 지정된 사분위수의 값을 보다 정확히 계산 
		static double GetQuantile(List<T> data, double quantile)
		{
			int n = data.Count;
			double rank = quantile * (n - 1);
			int lowerIndex = (int)Math.Floor(rank);
			int upperIndex = (int)Math.Ceiling(rank);
			double weight = rank - lowerIndex;

			if (lowerIndex == upperIndex)
			{
				return Convert.ToDouble(data[lowerIndex]);
			}
			else
			{
				return Convert.ToDouble(data[lowerIndex]) * (1 - weight) + Convert.ToDouble(data[upperIndex]) * weight;
			}
		}


	}
}
