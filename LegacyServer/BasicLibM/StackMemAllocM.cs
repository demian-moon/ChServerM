using System;
using System.Runtime.InteropServices;

namespace EcsServerLibM
{
	/// <summary>
	/// Native Heap, 즉 비-관리 힙으로부터 배열 메모리 할당
	/// GC가 일어나지 않는다!!!
	/// Async 함수, 람다식에서 사용 불가
	/// 
	/// using (StackMemAllocM<byte> sm = new StackMemAllocM<byte>(1024))
	/// {
	///     Span<byte> spBuf = sm.GetSpan();
	/// }
	///      
	/// </summary>
	/// <typeparam name="T"> struct 또는 null이 될 수 없는 값타입 이어야 한다 string은 null 이 될 수 있으므로 안됨 </typeparam>
	public unsafe ref struct StackMemAllocM<T> where T : unmanaged
	{
		int _size;
		IntPtr _ptr;

		public StackMemAllocM(int size)
		{
			_size = size;
			long lSize = _size;

			lSize *= sizeof(T);
			IntPtr bufSize = new IntPtr(lSize);

			_ptr = Marshal.AllocHGlobal(bufSize);
		}

		public Span<T> GetSpan()
		{
			return new Span<T>(_ptr.ToPointer(), _size);
		}

		public void Dispose()
		{
			if (_ptr == IntPtr.Zero)
				return;

			Marshal.FreeHGlobal(_ptr);
			_ptr = IntPtr.Zero;

		}
	}
}
