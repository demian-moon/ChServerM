using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EcsServerLibM
{
	public class ConcurrentSchedulerM
	{
		readonly object _lockObj = new object();

		SortedList<long, IExecutableM> _sortedList = new SortedList<long, IExecutableM>();


		public void Add(long executeTick, IExecutableM target)
		{
			lock (_lockObj)
			{
				_sortedList.Add(executeTick, target);
			}
		}

		public void ExecuteSchedule()
		{
			if (_sortedList.Count <= 0)
				return;

			KeyValuePair<long, IExecutableM> firstItem;
			while (true)
			{
				lock (_lockObj)
				{
					firstItem = _sortedList.First();
					if (firstItem.Key <= TickTimeM.GTick)
					{
						_sortedList.Remove(firstItem.Key);
					}
					else
					{
						return;
					}
				}
				firstItem.Value.Execute();

				if (_sortedList.Count <= 0)
					return;
			}
		}
	}


	public class ConcurrentSchedulerGroupM
	{
		int _iCntScheduler;

		ConcurrentSchedulerM[] _arrScheduler;

		public ConcurrentSchedulerGroupM(int iCntScheduler)
		{
			_iCntScheduler = iCntScheduler;
			_arrScheduler = new ConcurrentSchedulerM[iCntScheduler];

			for (int i = 0; i < iCntScheduler; i++)
			{
				_arrScheduler[i] = new ConcurrentSchedulerM();
			}
		}

		public void Add(long oid, long executeTick, IExecutableM target)
		{
			var idx = oid % _iCntScheduler;
			_arrScheduler[idx].Add(executeTick, target);
		}

		/// <summary>
		/// 병렬 실행
		/// </summary>
		public void ParallelExecuteSchedule()
		{
			Parallel.ForEach(_arrScheduler, scheduler => scheduler.ExecuteSchedule());

			//Parallel.ForEach(int i=0; i < _iCntScheduler; i++)
			//{
			//    _arrScheduler[i].ExecuteSchedule();
			//}
		}

	}




}
