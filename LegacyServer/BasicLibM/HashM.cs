using Collections.Pooled;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace EcsServerLibM
{

	internal class ExpireHashEventM : AbTimeEventBaseM
	{
		HashM _hashM;

		public ExpireHashEventM(HashM hashM, string jobId, long expireTimestamp) : base(jobId, expireTimestamp)
		{			
			_hashM = hashM;
		}

		public override IHasTimeEventsM Owner => _hashM;

		protected override void OnTerminate(string idJob)
		{	
			_hashM.Remove(idJob);
		}
	}


	public class HashM : IHasTimeEventsM
	{
		protected ConcurrentDictionary<string, string> _hash = new();
		ConcurrentDictionary<string, AbTimeEventBaseM> _timeEvents = new();

		public ConcurrentDictionary<string, AbTimeEventBaseM> TimeEvents
		{
			get
			{
				return _timeEvents;
			}

		}

		TimeEventSchedulerM _expireJobScheduler;

		public HashM(TimeEventSchedulerM expireJobScheduler)
		{
			_expireJobScheduler = expireJobScheduler;
		}
				

		/// <summary>
		/// 쿠키값 설정
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		public bool Set(string key, string value, int durationSec = -1)
		{
			if (durationSec == 0)
			{
				Debug.WriteLine("SetHash - Duration이 0이야!");
				return false;
			}

			// 이미 값이 있으면 value 변경, 기간 있었으면 cancel하고 다시 설정
			if (_hash.TryGetValue(key, out var oldVal))
			{
				_hash[key] = value;
				if(TimeEvents.ContainsKey(key))
					_expireJobScheduler.CancelJob(key);				
			}
			else
			{
				_hash.TryAdd(key, value);
			}

			if (durationSec != -1)  // 무제한 아니면 타임 이벤트 스케줄러 추가
			{
				var expireHashJob = new ExpireHashEventM(this, key, _expireJobScheduler.CreateExpirationTimestamp(durationSec * 1000));
				_expireJobScheduler.AddJob(expireHashJob);
			}
			return true;
		}

		/// <summary>
		/// 쿠키 지우기
		/// </summary>
		/// <param name="key"></param>
		/// <returns></returns>
		public bool Remove(string key)
		{
			if (_timeEvents.ContainsKey(key) )
			{
				_expireJobScheduler.CancelJob(key);
			}
			return _hash.TryRemove(key, out _);
		}

		public bool Has(string key)
		{
			return _hash.ContainsKey(key);
		}

		/// <summary>
		/// 쿠키값 얻기
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		public bool Get(string key, out string value)
		{
			return _hash.TryGetValue(key, out value);
		}

		/// <summary>
		/// 쿠키값을 얻어옴과 동시에 지우기
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		public bool GetAndRemove(string key, out string value)
		{
			if (_hash.TryGetValue(key, out value))
			{
				_hash.TryRemove(key, out _);
				return true;
			}
			return false;
		}

	}
}
