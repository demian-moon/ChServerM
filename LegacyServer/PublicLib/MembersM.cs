using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace EcsServerLibM
{

	// 옵저버가 Subscribe 할 때 생성해서 주는 Unsubscribe 객체 - 싱글쓰레드 List
	public class UnsubscriberM<T> : IDisposable
	{
		private LinkedList<IObserver<T>> _observers;
		private IObserver<T> _observer;

		public UnsubscriberM(LinkedList<IObserver<T>> observers, IObserver<T> observer)
		{
			_observers = observers;
			_observer = observer;
		}

		public void Dispose()
		{
			if (_observer != null && _observers.Contains(_observer))
				_observers.Remove(_observer);
		}
	}


	// 옵저버가 Subscribe 할 때 생성해서 주는 Unsubscribe 객체 - 멀티쓰레드 Concurrent
	public class ConcurrentUnsubscriberM<T> : IDisposable where T : IHasGameOid
	{
		private long _oid;
		private ConcurrentDictionary<long, IObserver<T>> _observers;

		public ConcurrentUnsubscriberM(long oid, ConcurrentDictionary<long, IObserver<T>> observers)
		{
			_observers = observers;
			_oid = oid;
		}

		public void Dispose()
		{
			if (_observers.ContainsKey(_oid))
				_observers.TryRemove(_oid, out IObserver<T> observer);
		}
	}

	// 옵저베이블 한 객체  - Subscribe 할때 위 Unsubscribe 가능한 객체를 리턴함
	public class ConcurrentObservableM<T> : IObservable<T> where T : class, IHasGameOid
	{
		private ConcurrentDictionary<long, IObserver<T>> _observers = new ConcurrentDictionary<long, IObserver<T>>();

		public IDisposable Subscribe(IObserver<T> observer)
		{
			long oid = (observer as T).Oid;
			_observers.TryAdd(oid, observer);

			return new ConcurrentUnsubscriberM<T>(oid, _observers);
		}
	}


	/// <summary>
	/// PkObjM을 멤버로 가지고 UserM을 Observer하는 클래스
	/// </summary>
	//public class MembersForPk<T> : ConcurrentSparseSetGetM<T> where T : PkObjM
	//{        
	//    public void SendPacketToMembers(PACKET_TYPE pkType, byte[] data, long bExceptOid = -1)
	//    {
	//        if (Count > 0)
	//        {
	//            foreach (PkObjM mem in ToArray())
	//            {
	//                if (mem.Oid != bExceptOid)
	//                    mem.SerializeSendPacket(pkType, data);
	//            }
	//        }
	//    }
	//}

	//public abstract class AbMembersForPkWithObsrvM<O> : MembersForPk<O>,  IObserver<O> where O : PkObjM
	//{
	//    public abstract void OnCompleted();
	//    public abstract void OnError(Exception error);
	//    public abstract void OnNext(O value);
	//}

	//public abstract class AbMembersForPkWithMultiObsrvM<O> : AbMembersForPkWithObsrvM<O> where O : PkObjM
	//{
	//    ConcurrentDictionary<uint, IDisposable> _dicUnsubscribe = new ConcurrentDictionary<uint, IDisposable>();

	//    public void Unsubscribe(uint oid)
	//    {
	//        _dicUnsubscribe.TryGetValue(oid, out IDisposable dis);
	//        dis?.Dispose();
	//    }

	//    public void AddUnsubscribe(uint oid, IDisposable unSubscribe)
	//    {
	//        _dicUnsubscribe.TryAdd(oid, unSubscribe);
	//    }

	//    public void UnsubscribeAll()
	//    {
	//        foreach(var dis in _dicUnsubscribe.Values)
	//        {
	//            dis.Dispose();
	//        }            
	//    }

	//}
}
