using System;
using System.Threading.Tasks.Dataflow;

namespace EcsServerLibM
{
	public abstract class ParallelExecuteM<T>
	{
		ActionBlock<T> _actBlock;

		public ParallelExecuteM(int maxDegreeOfParallelism)
		{
			_actBlock = new ActionBlock<T>(ParallelFunc, new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism });
		}

		void ParallelFunc(T targetObj)
		{
			try
			{
				_ParallelExecute(targetObj);
			}
			catch (Exception ex)
			{
				ex.ToString();
			}
		}

		public async void ParallelExecute(T targetObj)
		{
			await _actBlock.SendAsync(targetObj).ConfigureAwait(false);   // 패러럴하게 실행하고 싶은 함수를 가진 Target Object 전달            
		}
		/// <summary>
		/// 상속 구현 함수 - 직접 콜하지 않음
		/// </summary>
		/// <param name="targetObj"></param>
		/// <returns></returns>
		public abstract void _ParallelExecute(T targetObj);
	}
}
