using UnityEngine;
using System.Collections;

public class PlayMakerVariableUtil {

	public HutongGames.PlayMaker.FsmVariables SetFsmIntVariable(string intName, int iVar)
	{
		HutongGames.PlayMaker.FsmVariables fsmVar = new HutongGames.PlayMaker.FsmVariables();
		HutongGames.PlayMaker.FsmInt fsmInt = new HutongGames.PlayMaker.FsmInt();
		fsmInt.Name = intName;
		fsmInt.Value = iVar;
		HutongGames.PlayMaker.FsmInt [] fsmInts = new HutongGames.PlayMaker.FsmInt[1];
		fsmInts[0] = fsmInt;
		fsmVar.IntVariables = fsmInts;
		
		return fsmVar;
		
	}


	public HutongGames.PlayMaker.FsmVariables SetFsmIntVariable(string [] intNames, int [] iVars)
	{
		HutongGames.PlayMaker.FsmVariables fsmVar = new HutongGames.PlayMaker.FsmVariables();
		HutongGames.PlayMaker.FsmInt [] fsmInts = new HutongGames.PlayMaker.FsmInt[intNames.Length];

		for(int i=0; i<intNames.Length; i++)
		{
			HutongGames.PlayMaker.FsmInt fsmInt = new HutongGames.PlayMaker.FsmInt();
			fsmInt.Name = intNames[i];
			fsmInt.Value = iVars[i];
			fsmInts[i] = fsmInt;
		}
		fsmVar.IntVariables = fsmInts;		
		return fsmVar;
		
	}
}
