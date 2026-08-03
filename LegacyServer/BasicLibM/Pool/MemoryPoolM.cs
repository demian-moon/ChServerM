using Collections.Pooled;
using System.Collections.Concurrent;

namespace EcsServerLibM
{
	//using System.Net.Sockets;
	//using System.Threading.Tasks;
	//using System.IO.;

	//[StructLayout (LayoutKind.Sequential, Pack = 1)]


	// public class PipeM
	// {
	// 	async Task ProcessSomeThing(Socket s)
	// 	{
	// 		var pipe = new Pipe();
	// 		Task w = FillPipeAsync(s, pipe.Writer);
	// 		Task r = ReadPipeAsync(pipe.Reader);

	// 		await Task.WhenAll(w, r);			
	// 	}

	// 	async Task FillPipeAsync(Socket s, PipeWriter w)
	// 	{
	// 		int byteRead = await s.ReceiveAsync(w.GetMemory(512), SocketFlags.None);

	// 		TcpClient clnt;
	// 		NetworkStream stream = clnt.GetStream();


	// 	}

	// 	async Task ReadPipeAsync(PipeReader r)
	// 	{
	// 		ReadResult result = await r.ReadAsync();
	// 	}


	public abstract class AbConcurrentObjPoolM<T>
	{
		int _maxCapacity; // 최대 풀 사이즈, 필요시 조정 가능
		private readonly ConcurrentQueue<T> _objectQueue = new ConcurrentQueue<T>();
		
		public AbConcurrentObjPoolM(int maxCapacity = 0)
		{
			_maxCapacity = maxCapacity;
		}

		public T Rent()
		{
			return _objectQueue.TryDequeue(out T item) ? item : CreateInstance();
		}

		abstract public T CreateInstance();

		public void Return(T item)
		{
			if (_maxCapacity > 0 && _objectQueue.Count >= _maxCapacity)
				return;
			
			_objectQueue.Enqueue(item);
			//item = default(T);      // 기존 연결 끊기

		}

		
	}


	/// <summary>
	/// 범용적인 class 객체 풀, 추후 개선을 위해 1, 2, 4, 8, 16, 32 이런순으로 생성(?)이 필요한지 검토해 볼 필요가 있음 - new를 여러번 한꺼번에 하는게 의미가 있는지 부터
	/// </summary>
	/// <typeparam name="T"></typeparam>
	public class ConcurrentObjPoolM<T> : AbConcurrentObjPoolM<T> where T : new()
	{
		public override T CreateInstance()
		{
			return new T();
		}

	}


	///////////////////////////////////////////////////////////////////////////////////////
	/// <summary>
	///  일반 오브젝트 Pool
	/// </summary>
	/// <typeparam name="T"></typeparam>

	public abstract class AbObjPoolM<T>
	{
		int _maxCapacity; // 최대 풀 사이즈, 필요시 조정 가능
		private readonly PooledStack<T> _objectBag = new PooledStack<T>();

		public AbObjPoolM(int maxCapacity = 0)
		{
			_maxCapacity = maxCapacity;
		}

		public T Rent()
		{			
			return TryTake(out T item) ? item : CreateInstance();
		}

		public void Return(T item)
		{
			if (_maxCapacity > 0 && _objectBag.Count >= _maxCapacity)
			{
				return;
			}
			_objectBag.Push(item);
		}

		abstract public T CreateInstance();


		protected bool TryTake(out T item)
		{
			if (_objectBag.Count <= 0)
			{
				item = default(T);
				return false;
			}

			item = _objectBag.Pop();
			return true;
		}
	}




}
