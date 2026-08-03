using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EcsServerLibM
{
	public class NetWorkDelayM
	{
		public long lastSendServerTick; // 마지막 보낸 ServerTick
		long[] sortedArray; // 정렬된 배열 (이상치 제거를 위해 사용)

		private readonly object _locker = new object();


		ConcurrentQueue<long> delays = new ConcurrentQueue<long>();
		int windowSize; // 네트워크 딜레이 개수

		int _leftProcessCnt;

		public NetWorkDelayM(int windowSize)
		{
			this.windowSize = windowSize;
			sortedArray = new long[windowSize]; // 정렬된 배열 초기화
		}

		/// <summary>
		/// 유저에게 보낼 ServerTick 값을 리턴 값으로 얻는다
		/// </summary>
		/// <returns>유저에게 보낼 ServerTick 값</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public long SendServerTick()
		{
			// 이전 보낸 시간보다 작으면 안됨            
			var timeStamp = Stopwatch.GetTimestamp();

			var delaysCount = delays.Count;
			//if (delaysCount < windowSize) // 윈도우 사이즈만큼 딜레이가 늘어나기 때문에 clear 불필요
			//{
			//	Array.Clear(sortedArray, 0, sortedArray.Length);
			//}

			delays.CopyTo(sortedArray, 0); // 현재 딜레이 값을 정렬된 배열에 복사
			Array.Sort(sortedArray, 0, delaysCount); 

			var averageNetDelay = InterQuartileM<long>.RemoveOutliersAndAverage(sortedArray, delaysCount); // IQR 이상치 제거 후 평균 계산

			// 네트웍 딜레이 값을 제거하고 평균을 구함
			var curSendTick = timeStamp - averageNetDelay; // 현재 시간에서 네트워크 평균 딜레이를 빼서 보낼 ServerTick 계산
			if (curSendTick > lastSendServerTick)
			{
				lastSendServerTick = curSendTick;
				return curSendTick;
			}

			return timeStamp;
		}

		public void RecvServerTick()
		{
			if (lastSendServerTick <= 0)    // 예외 처리
				return;

			var curNetDelay = (Stopwatch.GetTimestamp() - lastSendServerTick) / 2;
			if (delays.Count >= windowSize)  // 윈도우 사이즈를 초과하면 가장 오래된 값을 제거
			{
				delays.TryDequeue(out _);
			}
			delays.Enqueue(curNetDelay);
			Interlocked.Increment(ref _leftProcessCnt); // 남겨진 처리 카운트 증가
		}

		//public long GetNetWorkDelay()
		//{
		//	var cleanData = InterQuartileM<long>.RemoveOutliersForSpeed(delays); // IQR 이상치 제거
		//																		 // Debug.WriteLine($"{DateTime.Now} - 지연시간 {cleanData.Average()}");
		//	if (cleanData.Count > 0)
		//		return (long)cleanData.Average();

		//	return 0;
		//}
		
	}
}
