using System;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Dataflow;

namespace EcsServerLibM
{

	/// <summary>
	/// 서버에서 서버유저들에게 쓰는 패킷 프로세스 그룹 (밖으로 보낼때, 들어온 memPk 처리)
	/// </summary>
	static class SendPacketGroupM
	{
		static Random random = new Random();
		static readonly int _iCntOutGoingBlock = SrvGlobal.cntOutGoingPkActBlock;
		static ConcurSeqTaskContextExecLongRunM<FinalPkDataM>[] _arrActBlockOutGoing;
		//static ActionBlock<FinalPkDataM>[] _arrActBlockOutGoing;


		static readonly int _iCntIncomeBlock = SrvGlobal.cntIncommingPkActBlock;
		static ConcurSeqTaskContextExecLongRunM<MemPacketM>[] _arrActBlockIncome;
		//static ActionBlock<MemPacketM>[] _arrActBlockIncome;

		static SendPacketGroupM()
		{
			// 서버에서 SendPacket - 밖으로 보내는 패킷 관련
			_arrActBlockOutGoing = new ConcurSeqTaskContextExecLongRunM<FinalPkDataM>[_iCntOutGoingBlock];
			//_arrActBlockOutGoing = new ActionBlock<FinalPkDataM>[_iCntOutGoingBlock];
			for (int i = 0; i < _iCntOutGoingBlock; i++)
			{
				_arrActBlockOutGoing[i] = new ConcurSeqTaskContextExecLongRunM<FinalPkDataM>(PacketM.SendPacket);
				//_arrActBlockOutGoing[i] = new ActionBlock<FinalPkDataM>(PacketM.SendPacket);
			}

			// 들어온 패킷을 memPk으로 보내는 함수
			_arrActBlockIncome = new ConcurSeqTaskContextExecLongRunM<MemPacketM>[_iCntIncomeBlock];
			//_arrActBlockIncome = new ActionBlock<MemPacketM>[_iCntIncomeBlock];
			for (int i = 0; i < _iCntIncomeBlock; i++)
			{
				_arrActBlockIncome[i] = new ConcurSeqTaskContextExecLongRunM<MemPacketM>(MemPkDispatcher.MemPkAction);
				//_arrActBlockIncome[i] = new ActionBlock<MemPacketM>(MemPkDispatcher.MemPkAction);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static public void SendPacket(long oid, in FinalPkDataM finalPkData)
		{
			var idx = oid % _iCntOutGoingBlock;
			_arrActBlockOutGoing[idx].Post(finalPkData);
		}

		/// <summary>
		/// 서버에서 SendPacket - 밖으로 보내는 패킷 관련
		/// </summary>
		/// <param name="oid"></param>
		/// <param name="packet"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static public void SendPacket(long oid, TcpClient tc, uint pid, PACKET_TYPE ePacketType, byte[] sendData, CompressAndEncryptM compEnc)
		{
			var idx = oid % _iCntOutGoingBlock;
			if (PacketM.TryMakeSendPacketData(tc, pid, ePacketType, sendData, compEnc, out FinalPkDataM finalPkData))
			{
				_arrActBlockOutGoing[idx].Post(finalPkData);
			}
		}


		/// <summary>
		/// 순서와 상관 없믄 패킷 보낼 때 (암호화 하지 않는다)
		/// </summary>
		/// <param name="tc"></param>
		/// <param name="pid"></param>
		/// <param name="ePacketType"></param>
		/// <param name="sendData"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static public async void SendPacketRnd(TcpClient tc, uint pid, PACKET_TYPE ePacketType, byte[] sendData, CompressAndEncryptM compEnc)
		{
			var idx = random.Next(0, _iCntOutGoingBlock - 1);
			if (PacketM.TryMakeSendPacketData(tc, pid, ePacketType, sendData, compEnc, out FinalPkDataM finalPkData))
			{
				_arrActBlockOutGoing[idx].Post(finalPkData);
			}
		}

		/// <summary>
		/// // 들어온 패킷을 memPk으로 보내는 함수
		/// </summary>
		/// <param name="oid"></param>
		/// <param name="memPk"></param>

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static public void SendMemPacket(long oid, MemPacketM memPk)
		{
			var idx = oid % _iCntIncomeBlock;
			_arrActBlockIncome[idx].Post(memPk);
		}

		/// <summary>
		/// 패킷 순서와 상관 없는 패킷 보낼 때
		/// </summary>
		/// <param name="memPk"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static public void SendMemPacketRnd(MemPacketM memPk)
		{
			var idx = random.Next(0, _iCntIncomeBlock - 1);
			_arrActBlockIncome[idx].Post(memPk);
		}
	}

}
