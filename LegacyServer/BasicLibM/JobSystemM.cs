using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EcsServerLibM
{

	/*
    public class Map
    {
        private Thread _updateThread;
        private bool _isRunning = true;

        public void Start()
        {
            _updateThread = new Thread(UpdateLoop);
            _updateThread.Start();
        }

        private void UpdateLoop()
        {
            while (_isRunning)
            {
                Update();
                Thread.Sleep(50); // 예를 들어, 20fps를 위해 50ms마다 업데이트
            }
        }

        public void Stop()
        {
            _isRunning = false;
            _updateThread.Join();
        }

        public void Update()
        {
            // 맵 업데이트 로직
            foreach (var obj in _objects)
            {
                obj.Update();
            }
        }

        private List<GameObject> _objects;
    }

    public void Update()
    {
        // 맵 업데이트 로직
        Parallel.ForEach(_objects, obj =>
        {
            obj.Update();
        });
    }



    public class JobSystemM
    {
        private BufferBlock<Action> _jobQueue = new BufferBlock<Action>();

        public JobSystemM()
        {
            // 워커 스레드 생성
            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                Task.Factory.StartNew(async () =>
                {
                    while (await _jobQueue.OutputAvailableAsync().ConfigureAwait(false))
                    {
                        var job = await _jobQueue.ReceiveAsync().ConfigureAwait(false);
                        job();
                    }
                }, TaskCreationOptions.LongRunning);
            }
        }

        public void QueueJob(Action job)
        {
            _jobQueue.Post(job);
        }
    }

    public class Map
    {
        private GameServer _server;
        private List<GameObject> _objects;

        public Map(GameServer server)
        {
            _server = server;
        }

        public void Update()
        {
            foreach (var obj in _objects)
            {
                _server.QueueJob(obj.Update);
            }
        }
    }


    // 세마포어 슬림 사용법

    using System.Threading;
    using System.Threading.Tasks;

    public class Map
    {
        private List<GameObject> _objects;
        private SemaphoreSlim _semaphore = new SemaphoreSlim(Environment.ProcessorCount);

        public void Update()
        {
            Parallel.ForEach(_objects, async obj =>
            {
                await _semaphore.WaitAsync();
                try
                {
                    obj.Update();
                }
                finally
                {
                    _semaphore.Release();
                }
            });
        }
    }

    */


	public class UniqueBufferBlock<T>
	{
		private BufferBlock<T> _buffer;
		private ConcurrentDictionary<T, bool> _addedItems;

		public UniqueBufferBlock()
		{
			_buffer = new BufferBlock<T>();
			_addedItems = new ConcurrentDictionary<T, bool>();
		}

		public void Post(T item)
		{
			if (_addedItems.TryAdd(item, true))
			{
				_buffer.Post(item);
			}
			else
			{
				Console.WriteLine($"중복된 숫자 '{item}'는 추가할 수 없습니다.");
			}
		}

		public async Task SendAsync(T item)
		{
			if (!_addedItems.TryAdd(item, true))
			{
				Console.WriteLine($"중복된 숫자 '{item}'는 추가할 수 없습니다.");
				return;
			}

			await _buffer.SendAsync(item).ConfigureAwait(false);
		}

		public Task<bool> OutputAvailableAsync()
		{
			return _buffer.OutputAvailableAsync();
		}

		public async Task<T> ReceiveAsync()
		{
			T item = await _buffer.ReceiveAsync().ConfigureAwait(false);
			_addedItems.TryRemove(item, out _);
			return item;
		}

	}


}


