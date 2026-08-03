using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace EcsServerLibM
{
	/// <summary>
	/// 
	/// </summary>
	/// <typeparam name="T">타겟이 되는 객체 타입</typeparam>

	public class EtdTaskM<A> : IExecutableM
	{
		A _arg;

		Action<A> _funcHandler;

		public EtdTaskM(Action<A> funcHandler, in A arg)
		{
			_funcHandler = funcHandler;
			_arg = arg;
		}

		public void Execute()
		{
			_funcHandler(_arg);
		}
	}




	public class ExecutableTaskDispatcherM
	{

		static ThreadLocal<Queue<ExecutableTaskDispatcherM>> tls_EtdQue = new ThreadLocal<Queue<ExecutableTaskDispatcherM>>(() => new Queue<ExecutableTaskDispatcherM>());
		static ThreadLocal<ExecutableTaskDispatcherM> tls_CurEtdOccupyingThread = new ThreadLocal<ExecutableTaskDispatcherM>();

		ConcurrentQueue<IExecutableM> taskQue = new ConcurrentQueue<IExecutableM>();
		int iCntRemainTask;



		void FlushTask()
		{
			int iTaskCount;
			do
			{
				iTaskCount = taskQue.Count;
				for (int i = 0; i < iTaskCount; ++i)
				{
					taskQue.TryDequeue(out IExecutableM task);
					task.Execute();
				}
			} while (Interlocked.Add(ref iCntRemainTask, -iTaskCount) != 0); // Enqueue 풀기
		}


		/// <summary>
		/// Task 실행
		/// EnqueueAndProcess
		/// </summary>
		/// <param name="task"></param>
		public void DoTask(IExecutableM task)
		{
			if (Interlocked.Increment(ref iCntRemainTask) != 1) // 1이었으면 계속 
			{
				taskQue.Enqueue(task);
			}
			else
			{
				taskQue.Enqueue(task);

				if (tls_CurEtdOccupyingThread.Value != null)
				{
					tls_EtdQue.Value.Enqueue(this);
				}
				else
				{
					tls_CurEtdOccupyingThread.Value = this;
					FlushTask();

					while (tls_EtdQue.Value.Count != 0)
					{
						var etd = tls_EtdQue.Value.Dequeue();
						etd.FlushTask();
					}

					tls_CurEtdOccupyingThread.Value = null;
				}
			}
		}
	}
}