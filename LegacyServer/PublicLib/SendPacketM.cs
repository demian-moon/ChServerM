using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EcsServerLibM
{

	/// <summary>
	/// UserM에서 보내거나 받는 패킷에 사용
	/// </summary>
	static class SendPacketM
	{
		// 밖으로 보내는 Packet
		//static ActionBlock<FinalPkDataM> _sendPkActBlock = new ActionBlock<FinalPkDataM>(PacketM.SendPacket);
		static ConcurSeqTaskContextExecLongRunM<FinalPkDataM> _sendPkActBlock = new ConcurSeqTaskContextExecLongRunM<FinalPkDataM>(PacketM.SendPacket);

		// 들어온 memPk
		//static ActionBlock<MemPacketM> _memPkActBlock = new ActionBlock<MemPacketM>(MemPkDispatcher.MemPkAction);
		static ConcurSeqTaskContextExecLongRunM<MemPacketM> _memPkActBlock = new ConcurSeqTaskContextExecLongRunM<MemPacketM>(MemPkDispatcher.MemPkAction);

		// 들어온 memPk UI		
		static ActionBlock<MemPacketM> _memPkActBlockUI = new ActionBlock<MemPacketM>(MemPkDispatcher.MemPkAction,	
			new ExecutionDataflowBlockOptions {	TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext() });

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static public void SendPacket(in FinalPkDataM finalPkData)
		{
			_sendPkActBlock.Post(finalPkData);
		}


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static public void SendPacket(PacketM packet)
		{
			if (PacketM.TryMakeSendPacketData(packet, out FinalPkDataM finalPkData) == true)
			{
				_sendPkActBlock.Post(finalPkData);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static public void SendPacket(TcpClient tc, uint pid, PACKET_TYPE ePacketType, byte[] sendData, CompressAndEncryptM compEnc = null)
		{
			if (PacketM.TryMakeSendPacketData(tc, pid, ePacketType, sendData, compEnc, out FinalPkDataM finalPkData) == true)
			{
				_sendPkActBlock.Post(finalPkData);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static public void SendMemPacket(MemPacketM memPk)
		{
			_memPkActBlock.Post(memPk);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static public void SendMemPacketUI(MemPacketM memPk)
		{
			_memPkActBlockUI.Post(memPk);
		}


	}

}
