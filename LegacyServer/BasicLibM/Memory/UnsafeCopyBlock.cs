using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace EcsServerLibM
{
	/// <summary>
	/// 추후 살려서 사용하자 unsafe 빌드 옵션에 추가 해야 됨 - 가장 빠른 카피 방법

	//static public class UnsafeCopyBlockM
	//{
	//	static  public void Copy(byte[] srcBuf, byte[] destBuf, int count)
	//	{
	//		unsafe
	//		{
	//			fixed (byte* src = srcBuf)
	//			fixed (byte* dst = destBuf)
	//			{
	//				Unsafe.CopyBlock(dst, src, (uint)count);
	//			}
	//		}
	//	}
	//}
}
