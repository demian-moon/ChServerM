using System;


namespace EcsServerLibM
{
	public class CryptM
	{
		const int C1 = 52845;
		const int C2 = 22719;
		const int C3 = 72957;

		static public void Encrypt(Span<byte> source, out byte[] dest)
		{
			if (source.Length == 0)
			{
				dest = null;
				return;
			}

			int iShift;
			int k = C3;
			dest = new byte[source.Length];
			for (int i = 0; i < dest.Length; i++)
			{
				iShift = k % 9;
				dest[i] = (byte)((int)source[i] ^ (k >> iShift));
				k = dest[i] * C1 + C2;
			}
			return;
		}

		static public void Decrypt(Span<byte> source, out byte[] dest)
		{
			if (source.Length == 0)
			{
				dest = null;
				return;
			}

			int iShift;
			int k = C3;
			dest = new byte[source.Length];
			for (int i = 0; i < dest.Length; i++)
			{
				iShift = k % 9;
				dest[i] = (byte)((int)source[i] ^ (k >> iShift));
				k = source[i] * C1 + C2;
			}
			return;
		}
	}
}
