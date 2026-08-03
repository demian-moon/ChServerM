namespace EcsServerLibM
{
	/* 예제
    
    목적 : 멀티쓰레드 상의 데이터 Set에 대한 동기화 문제 때문에 ActionBlock을 통해 단일 쓰레드에서 값을 변경하는 로직을 구현한 일련의 제네릭 클래스 묶음이다.

           1. enum으로 정의된 명령(CMD - 타겟에서 실행할 함수와 매칭됨)과  대상(Target) 클래스, 그리고 함수를 실행할 때 필요한 매개변수값을 정의한 --  AbCmdArgM을 상속 구현한다.
           2. AbCmdMachine<E, T, A>를 상속 구현한다.   
              - C - enum으로 정의된 명령(CMD)
              - T : 데이터 변경 대상(Target)이되는 클래스 타입
              - A : 1번에서 상속 구현한 AbCmdArgM을 상속받은 실제 타입
              - AddCmdAction을 오버라이드 해서 아래 3번에서 구현한 CmdActionM을 Dictinary에 추가 (명령(CMD) - CmdAction연결 지점 
              - 
           3. AbCmdActionM<C, T, A>을 상속 받아 실제 명령(CMD)와 매칭될 실행 클래스들 구현
                - Run 함수 오버라이드 해서 매개변수로 넘겨지는 cmdArg를 사용해서 cmdArg.TargetObject.함수( cmdArg가 1번에서 만든 클래스이므로 매개변수를 얻어서 셋하거나 실행함)
          
        
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////           
    < 클래스 설명 >
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    AbCmdMachine<C, T, A> : Cmd로 Target 클래스의 데이터를 변경을 주관하는 클래스 
        이 클래스를 상속받아 enum으로 정의된 명령을 전달했을 때 실행할 CmdAction들을 추가한다.
        
        
        // ex) ClaTang 클래스에서 AbCmdMachine<ECmdTangCla, TangCla, CmdArgTangCla>을 상속받은 CmdMachineTangCla를 생성하고 아래와 같이 사용한다.

        멤버 변수로 선언 : CmdMachineTangCla<ECmdTangCla, TangCla, CmdArgTangCla> cmdMachine = new CmdMachineTangCla<ECmdTangCla, TangCla, CmdArgTangCla>()


        var arg = cmdMachine.RentCmdArg();  <---- !!!!! 매우 중요 빈번하게 call 될 가능성 때문에 매개변수를 담는 RentCmdArg는 반드시 Rent해서 쓴다 - 내부에서 Pool 처리 중!!

        arg.SetCmd(ECmdTangCla.SET_NAME);               // 실행할 Cmd는 SET_NAME 
        arg.name = "choice최";                        // 이름을 셋하는 함수를 호출 할 예정이므로 string을 셋함
            
        cmdMachine.RunCmdAction(arg);                 // arg를 넘겨서 실행

        


    AbCmdArgM  : 이 클래스를 상속 받아 특정 Target 클래스 변경하는데 필요한 모든 arg값을 정의한다.      
    
        1. 멤버 변수로 어떤 명령을 실행할 지 enum으로 정의 돼  들어가 있다
        2. 멤버 변수로 실제 데이터를 변경할 TargetObj가 있다.     
        3. 상속받아서 TargetObj의 데이터를 변경할때 전달 해야되는 매개변수를 추가해서 구현한다. ---- 하나의 클래스를 계속 Pool로 재사용하므로 예를 들어 ClaTang의 모든 Set하는 함수들의 매개변수를 전달 할 수 있는 형태여야 한다.!!!
        
    
    CmdArgPoolM : 위 AbCmdArgM을 상속받은 클래스를 재사용하기 위한 Pool
    

    AbCmdActionM : 이 클래스를 상속받아 여러가지 Target의 데이터를 변경할 실제 CmdAction들을 만든다.
    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////



    // 실제 클래스에서 사용 예 ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

    // 명령으로 보낼  
    public enum ECmdTangCla
    {
        SET_NAME,
    }



    public class CmdArgTangCla : AbCmdArgM<ECmdTangCla, TangCla>
    {
        public string name;
    }

    public class CmdMachineTangCla : AbCmdMachine<ECmdTangCla, TangCla, CmdArgTangCla>
    {
        public override void AddCmdAction(ConcurrentDictionary<ECmdTangCla, AbCmdActionM<ECmdTangCla, TangCla, CmdArgTangCla>> dicCmdAction)
        {
            var cmdAct = new SetNameCmdAction(ECmdTangCla.SET_NAME);
            dicCmdAction.TryAdd(cmdAct.Cmd, cmdAct);
        }
    }


    public class SetNameCmdAction : AbCmdActionM<ECmdTangCla, TangCla, CmdArgTangCla>
    {
        public SetNameCmdAction(ECmdTangCla cmd) : base(cmd) { }

        public override async Task Run(CmdArgTangCla cmdArg)
        {
            cmdArg.TargetObj._user.Id = cmdArg.name;           <---- 설명 3번 예
            Debug.WriteLine($"{cmdArg.name} 이 이름이래 --> cmd로 userId 셋한다 {cmdArg.TargetObj._user.Id}");
        }
    }

   */


	///// <summary>
	///// 커맨드Arg 클래스
	/////  AbCmdArgM  : 이 클래스를 상속 받아 특정 Target 클래스 변경하는데 필요한 모든 arg값을 정의한다.      
	/////  1. 멤버 변수로 어떤 명령을 실행할 지 enum으로 정의 돼 들어가 있다
	/////  2. 멤버 변수로 실제 데이터를 변경할 TargetObj가 있다.
	/////  3. 상속받아서 TargetObj의 데이터를 변경할때 전달 해야되는 매개변수를 추가해서 구현한다. 
	/////     - 하나의 클래스를 계속 Pool로 재사용하므로 예를 들어 ClaTang의 모든 Set하는 함수들의 매개변수를 전달 할 수 있는 형태여야 한다.!!!
	///// </summary>
	///// <typeparam name="C"> 커맨드 Enum </typeparam>
	///// <typeparam name="T"> 실제 함수를 실행할 Target 클래스 타입</typeparam>
	//public class AbCmdArgM<C, T> where C : Enum
	//{
	//    public C Cmd { get; set; }

	//    T _targetObj;                
	//    public T TargetObj 
	//    { 
	//        get 
	//        { if (_targetObj == null)
	//                Debug.WriteLine($"{typeof(T)} 타입의 TagerObj가 설정되지 않음: SetCmdTaget() 함수 사용해서 set했는지 확인");

	//            return _targetObj;
	//        }
	//        set { _targetObj = value; } 
	//    } 

	//    // 초기화
	//    virtual public void Clear()
	//    {
	//        Cmd = default(C);
	//        //TargetObj = default(T);
	//    }

	//    //public void SetCmdTarget(C cmd, T targetObj)
	//    //{
	//    //    Cmd = cmd;
	//    //    TargetObj = targetObj;
	//    //}

	//    public void SetCmd(C cmd)
	//    {
	//        Cmd = cmd;            
	//    }

	//    public void SetTarget(T targetObj)
	//    {
	//        TargetObj = targetObj;
	//    }
	//}

	///// <summary>
	///// 커맨드 ArgPool - 매번 생성하고 없애는게 부담스러우니 Pool에서 rent해서 쓰고 리턴한다.
	///// CmdArgPoolM : AbCmdArgM을 상속받은 클래스를 재사용하기 위한 Pool
	///// </summary>
	///// <typeparam name="C"> 커맨드 Enum </typeparam>
	///// <typeparam name="T">커맨드를 실행하는 주체 Target 클래스 타입</typeparam>
	///// <typeparam name="A">커맨드 Arg 타입 AbCmdArgM<C, T>를 상속 받아야 함 </typeparam>
	//public class CmdArgPoolM<C, T, A> where C : Enum where A : AbCmdArgM<C, T>, new()
	//{
	//    private readonly ConcurrentBag<A> _argBag = new ConcurrentBag<A>();

	//    public void Return(A item)
	//    {
	//        item.Clear();   // 다 초기화
	//        _argBag.Add(item);            
	//        item = default(A);      // 기존 연결 끊기
	//    }

	//    private bool TryTake(out A item)
	//    {
	//        return _argBag.TryTake(out item);
	//    }

	//    public A Rent()
	//    {
	//        return TryTake(out A item) ? item : new A();
	//    }
	//}


	//// 
	///// <summary>
	///// AbCmdMachine<C, T, A> : 이 클래스를 상속받아 enum으로 정의된 명령을 전달했을 때 실행할 CmdAction들을 추가한다.    
	///// 커맨드 머신
	/////     1. 커맨드와 커맨드 액션에 대한 dic 보유
	/////     2. 커맨드Arg를 받아서 RunCmdAction을 실행 - 커맨드Arg에는 커맨드와 실행시킬 대상과 매개변수가 있음    
	///// </summary>
	///// <typeparam name="C"> 커맨드 Enum </typeparam>
	///// <typeparam name="T">커맨드를 실행하는 주체 Target 클래스 타입</typeparam>
	///// <typeparam name="A">커맨드 Arg 타입 AbCmdArgM<C, T>를 상속 받아야 함 </typeparam>
	//public abstract class AbCmdMachine<C, T, A> where C : Enum where A : AbCmdArgM<C, T>, new()
	//{
	//    T _targetObj;

	//    ActionBlock<A> _cmdActionBlock;
	//    ConcurrentDictionary<C, AbCmdActionM<C, T, A>> _dicCmdAction = new();
	//    CmdArgPoolM<C, T, A> _cmdArgPool = new();

	//    public AbCmdMachine(T targetObj)
	//    {
	//        _targetObj = targetObj;
	//        _cmdActionBlock = new ActionBlock<A>(RunCmdAct);
	//        LoadCmdAction();
	//    }

	//    public A RentCmdArg()
	//    {
	//        A arg = _cmdArgPool.Rent();  // cmdArg Rent
	//        arg.SetTarget(_targetObj);
	//        return arg;
	//    }


	//    /// <summary>
	//    /// 이 함수를 통해서 명령 실행 
	//    /// 매개변수로 넘길 cmdArg는 생성해서 넘기는게 아니라 RentCmdArg()를 통해서 반드시 렌트해서 채우고 넘길 것
	//    /// </summary>
	//    /// <param name="cmdArg"></param>
	//    /// <returns></returns>
	//    public async Task RunCmdAction(A cmdArg)
	//    {
	//        await _cmdActionBlock.SendAsync(cmdArg).ConfigureAwait(false);
	//    }


	//    private async Task RunCmdAct(A cmdArg)
	//    {
	//        if (_dicCmdAction.TryGetValue(cmdArg.Cmd, out AbCmdActionM<C, T, A> cmdAct))
	//        {
	//            cmdAct.Run(cmdArg);
	//            _cmdArgPool.Return(cmdArg); // cmdArg클래스 회수
	//        }
	//        else
	//        {
	//            Debug.WriteLine("RunCmdAction 예약된 명령이 없어");
	//        }
	//    }


	//    public abstract void AddCmdAction(ConcurrentDictionary<C, AbCmdActionM<C, T, A>> dicCmdAction);

	//    private void LoadCmdAction()
	//    {            
	//        AddCmdAction(_dicCmdAction);
	//    }
	//}


	///// <summary>
	///// AbCmdActionM : 이 클래스를 상속받아 여러가지 Target의 데이터를 변경할 실제 CmdAction들을 만든다.
	///// 커맨드 액션
	/////     1. 커맨드와 그에 따른 실행Run 구현이 있는 추상 클래스
	/////     2. 커맨드Arg를 통해서 target과 매개변수를 넘겨 받으면 단순히 target의 메소드를 실행해준다    
	///// </summary>
	///// <typeparam name="T"></typeparam>
	//public abstract class AbCmdActionM<C, T, A> where C : Enum
	//{
	//    public C Cmd { get; set; }

	//    public AbCmdActionM(C cmd)
	//    {
	//        Cmd = cmd;
	//    }

	//    public abstract Task Run(A cmdArg);
	//}


}
