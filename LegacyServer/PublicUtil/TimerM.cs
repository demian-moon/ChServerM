using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace EcsServerLibM
{

	public enum TIMER_TYPE
	{
		/*공용*/
		DISCONNECT_USER_FORCE = 40000,
		/* 서버 쪽 */
		HEART_BIT_SEND,
		HEART_BIT_ALIVE_CHECK,
		SERVER_TICK_SEND,
		MAP_TICK_SCRIPT,
		MAP_UPDATE,
		MON_TICK_SCRIPT,
		TIME_SCHEDULER,
		/* 앱 쪽 */


		/* 클라 쪽 */

	}
	public class TimerM<T>
	{
		ConcurrentDictionary<T, Timer> _dicTimer = new();


		// 
		/// <summary>
		/// 타이머 추가 이미 있으면 업데이트
		/// </summary>
		/// <param name="timerType"></param>
		/// <param name="timerAction"></param>
		/// <param name="dueTimeSpan">호출하기전 Delay값 ex)  TimeSpan.FromMilliseconds() </param>
		/// <param name="periodTimeSpan">호출 간격, 한번만 보내고 싶으면 Timeout.InfiniteTimeSpan</param>
		public void AddOrUpdateTimer(T timerType, ITimerActionM timerAction, TimeSpan dueTimeSpan, TimeSpan periodTimeSpan)
		{
			_dicTimer.AddOrUpdate(timerType,
				timer =>
				{
					return new Timer(obj =>
					{
						((ITimerActionM)obj).DoAction();
					}, timerAction, dueTimeSpan, periodTimeSpan);
				},
				(key, existingTimer) =>
				{
					try
					{
						existingTimer.Change(dueTimeSpan, periodTimeSpan);
						return existingTimer;
					}
					catch (ObjectDisposedException)
					{
						return new Timer(obj =>
						{
							((ITimerActionM)(obj)).DoAction();
						}, timerAction, dueTimeSpan, periodTimeSpan);
					}
				});
		}

		//public Timer UpdateTimer(eTimerMType timerType, Timer timer)
		//{
		//    timer.Change(dueTimeSpan, periodTimeSpan);
		//}

		public void ChangeTimer(T timerType, TimeSpan dueTimeSpan, TimeSpan periodTimeSpan)
		{
			_dicTimer.AddOrUpdate(timerType,
			timer =>
			{
				// 타이머가 없으면 새로운 타이머를 만들 수 있습니다. 여기서는 새로운 타이머를 만들지 않도록 null 반환
				Debug.WriteLine("타이머가 없음요-------놀라지마시오 최초!! ChangeTimer로 변경하려하면 불릴 수 있으니");
				return null;
			},
			(key, existingTimer) =>
			{
				try
				{
					existingTimer.Change(dueTimeSpan, periodTimeSpan);
				}
				catch (ObjectDisposedException e)
				{
					Debug.WriteLine($"ChangeTimer 타이머 이미 Disposed: {e.Message}");
				}
				return existingTimer; // 기존 타이머 반환
			});
		}

		public void RemoveTimer(T timerType)
		{
			if (_dicTimer.TryRemove(timerType, out Timer tmTimer) == true)
			{
				tmTimer.Dispose();
			}
			else
			{
				Debug.WriteLine("지우려는 타이머가 없음요------------");
			}
		}

		public void DisposeAllTimer()
		{
			foreach (T timerType in _dicTimer.Keys)
			{
				if (_dicTimer.TryRemove(timerType, out Timer tmTimer) == true)
				{
					tmTimer.Dispose();
				}
			}
		}
	}






	// 타이머 액션 인터 페이스
	public interface ITimerActionM
	{

		Task DoAction();
	}

	//////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
	/// <summary>
	///  공용 타이머 관련 Action
	/// </summary>
	public class TimerM_User_Disconnect_Force : ITimerActionM
	{
		InnerUserM _user;

		public TimerM_User_Disconnect_Force(InnerUserM user)
		{
			_user = user;
		}

		public async Task DoAction()
		{
			_user.RemoveTimer(TIMER_TYPE.DISCONNECT_USER_FORCE);

			if (_user.Tc.Connected == true)
			{
				try
				{
					// _user.RequestDisconnectForce();   // 이미 해서 이 타이머 실행이라 직접 Tc.Close 한다
					_user.Tc.Close();
				}
				catch (Exception ex)
				{
					Debug.WriteLine("타이머로 Disconnect Error:" + ex.Message);
				}
			}
		}
	}
}
