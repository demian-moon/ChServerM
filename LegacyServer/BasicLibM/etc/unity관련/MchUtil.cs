using UnityEngine;
using System.Collections;

public class MchUtil {

	private static MchUtil _instance = null;

	private MchUtil() {}

	public static MchUtil Instance  /* singleTone */
	{
		get
		{
			if( _instance == null)
			{
				return _instance = new MchUtil();
			}
			return _instance;
		}
	}

	public float PixelToUnityM (int pixel, int screenHeight, int orthoGraphicSize)
	{
		return (float)(pixel * (orthoGraphicSize *2)) / (float)screenHeight;
	}

	public float UnityMToPixel (int unityM, int screenHeight, int orthoGraphicSize)
	{
		return (float)screenHeight / (float)( orthoGraphicSize * 2) * (float)unityM;
	}

	public int LogicalWidthToRealWidth(int logicalWidth, int realWidth, int iLogicalVar)
	{
		int iReal = (int)( (float)iLogicalVar / ( (float)logicalWidth / (float)realWidth ) );
		return iReal;
	}

	public int LogicalHeightToRealHeight(int logicalHeight, int realHeight, int iLogicalVar)
	{
		int iReal = (int)( (float)iLogicalVar / ( (float)logicalHeight / (float)realHeight ) );
		return iReal;
	}


	/* Barycentric 알고리즘을 써서 세점안에 한점이 있는지 검사하는 함수 - made by 강명훈 */
	public bool IsPointInTri(Vector2 v1, Vector2 v2, Vector2 v3, Vector2 point)
	{
		
		// coordinate 값 계산
		float a1 = ((v2.y - v3.y)*(point.x - v3.x) + (v3.x - v2.x)*(point.y - v3.y)) / ((v2.y - v3.y)*(v1.x - v3.x) + (v3.x - v2.x)*(v1.y - v3.y));
		float a2 = ((v3.y - v1.y)*(point.x - v3.x) + (v1.x - v3.x)*(point.y - v3.y)) / ((v2.y - v3.y)*(v1.x - v3.x) + (v3.x - v2.x)*(v1.y - v3.y));
		float a3 = 1 - a1 - a2;
		
		// coordinate를 통한 포인터의 위치 파악
		if (0 <= a1 && a1 <= 1){
			if (0 <= a2 && a2 <= 1){
				if (0 <= a3 && a3 <= 1){
					return true;		// Point 가 삼각형 안에 있을때 true 반환
				}
			}
		}
		
		return false;
	}

    public Quaternion LookAtToPos(GameObject obj, Vector3 Pos, string objForward = "z")
    {
        Vector3 forward = Vector3.zero;
        if (objForward == "x")
        {
            forward = Vector3.right;
        }
        else if (objForward == "y")
        {
            forward = Vector3.up;
        }
        else if (objForward == "z")
        {
            forward = Vector3.forward;
        }

        Vector3 worldDir = obj.transform.TransformDirection(forward);
        Vector3 vLookAt = Pos - obj.transform.position;
        Quaternion qLookAt = Quaternion.FromToRotation(worldDir, vLookAt) * obj.transform.rotation;
        obj.transform.rotation = qLookAt;

        return qLookAt;
    }

    /// <summary>
    /// 월드 좌표들을 받고 Screen좌표로 변환해서 덮어 쓴다 - Vector는 새로 생성하지 않으니 주의 (속도를 위해서)
    /// </summary>
    /// <param name="worldPos"></param>
    /// <returns></returns>
    public void ConvertWorldToScreenPositions(Vector3 [] worldPos)
    {        
        for(int i=0; i<worldPos.Length; i++)
        {
            Vector3 tmpScreen = Camera.main.WorldToScreenPoint(worldPos[i]);
            worldPos[i] = new Vector3(tmpScreen.x, tmpScreen.y, 0f);
        }
    }

    /// <summary>
    /// 2점과 y값을 받고 x값을 찾아서 해당 vector를 리턴
    /// </summary>
    /// <param name="p1"></param>
    /// <param name="p2"></param>
    /// <param name="inY"></param>
    /// <returns></returns>
    static public Vector2 LinearEquationX(Vector2 p1, Vector2 p2, float inY)
    {

        float slope = (p1.y - p2.y) / (p1.x - p2.x);
        float b = -(slope * p1.x) + p1.y;

        float x = (inY - b) / slope;

        return new Vector2(x, inY);
    }

    /// <summary>
    /// 2점과 x값을 받아서 해당 vector를 리턴
    /// </summary>
    /// <param name="p1"></param>
    /// <param name="p2"></param>
    /// <param name="inX"></param>
    /// <returns></returns>
    static public Vector2 LinearEquationY(Vector2 p1, Vector2 p2, float inX)
    {

        float slope = (p1.y - p2.y) / (p1.x - p2.x);
        float b = -(slope * p1.x) + p1.y;

        float y = slope * inX + b;

        return new Vector2(inX, y);

    }



    /// <summary>
    /// 방향 벡터와 평행하면서 pos점을 지나고 x축과 만나는 점의 좌표를 리턴한다
    /// </summary>
    /// <param name="dir">방향 벡터</param>
    /// <param name="pos">방향 벡터와 평행하며 pos를 지나는 벡터를 만들기 위해서 </param>
    /// <param name="axisX">x축 값</param>
    /// <returns></returns>
    static public Vector3 GetVectorPtsAcrossAxisX(Vector2 dir, Vector2 pos, float axisX)
    {

        /* pos를 지나면서 dir에 평행한 점 - 방향은 dir과 같이 유지 됨*/
        Vector2 pt = dir + pos;

        /* pos와 pt를 지나면서 axisX와 만나는 점 */
        Vector3 crossPts = MchUtil.LinearEquationY(pos, pt, axisX);

        return crossPts;
    }

    /// <summary>
    /// 방향 벡터와 평행하면서 pos점을 지나고 y축과 만나는 점의 좌표를 리턴한다
    /// </summary>
    /// <param name="dir">방향 벡터</param>
    /// <param name="pos">방향 벡터와 평행하며 pos를 지나는 벡터를 만들기 위해서 </param>
    /// <param name="axisY">y축 값</param>
    /// <returns></returns>
    static public Vector3 GetVectorPtsAcrossAxisY(Vector2 dir, Vector2 pos, float axisY)
    {
        /* pos를 지나면서 dir에 평행한 점 */
        Vector2 pt = dir + pos;

        /* pos와 pt를 지나면서 axisY와 만나는 점 */
        Vector3 crossPts = MchUtil.LinearEquationX(pos, pt, axisY);

        return crossPts;
    }



    /// <summary>
    /// 기준이 되는 방향 벡터(_dirWind)를 가지고 특정 점(pos)을 지나고 평행이며 
    /// 주어진 Rect와 만나는  두 점의 배열을 만들어 낸다
    /// </summary>

    static public Vector3[] GetVectorPtsAcrossRect(Vector2 dir, Vector2 pos, Rect rt)
    {

        Vector2 svx;
        Vector2 evx;

        Vector2 svy;
        Vector2 evy;


        /* st 벡터의 방향에 따른 좌우 좌표(밖으로 나가는 기준) */
        if (dir.x <= 0)
        {
            svx = MchUtil.GetVectorPtsAcrossAxisX(dir, pos, rt.xMax);
            evx = MchUtil.GetVectorPtsAcrossAxisX(dir, pos, rt.xMin);
        }
        else
        {
            svx = MchUtil.GetVectorPtsAcrossAxisX(dir, pos, rt.xMin);
            evx = MchUtil.GetVectorPtsAcrossAxisX(dir, pos, rt.xMax);
        }

        if (dir.y <= 0)
        {
            svy = MchUtil.GetVectorPtsAcrossAxisY(dir, pos, rt.yMin);
            evy = MchUtil.GetVectorPtsAcrossAxisY(dir, pos, rt.yMax);
        }
        else
        {
            svy = MchUtil.GetVectorPtsAcrossAxisY(dir, pos, rt.yMax);
            evy = MchUtil.GetVectorPtsAcrossAxisY(dir, pos, rt.yMin);
        }

        Vector2 startVt = ((pos - svx).sqrMagnitude <= (pos - svy).sqrMagnitude) ? svx : svy;
        Vector2 endVt = ((pos - evx).sqrMagnitude <= (pos - evy).sqrMagnitude) ? evx : evy;

        Vector3[] pts = new Vector3[] { startVt, endVt };

        return pts;
    }



}

/* pickable Tiles Lib - 피킹 가능한 타일 라이브러리 */
public interface IHasWorldRectTile		/* 해당 타일이 상속 받아 구현 해야 함 */
{
	Rect WorldRect{get;}
	Vector3 _pos{get; set;}
}

/* 해당 맵의 이터레리터가 상속 받아서 구현해야 함 - 순서는 레이어 역순 (가장 앞에 있는 레이어를 가진 타일이 먼저 반환되어야 함 */	
public interface IBackwardIterator	
{
	bool HasNext();
	IHasWorldRectTile Next();
}

public interface IPickableMap	/* 해당 맵에서 상속 받아 구현 해야 함 */
{
	IBackwardIterator CreateBackwardTileIterator();
}

/* 타일 피킹 클래스 */
public class TilePicking
{
	IPickableMap _map;
	public TilePicking(IPickableMap map)
	{
		_map = map;
	}
	
	public bool PickingTilePos(Vector3 touchWorldPos, out Vector3 touchTilePos)
	{
		touchTilePos = new Vector3(-1f, -1f, -1f);
		IBackwardIterator iter = _map.CreateBackwardTileIterator();
		
		while(iter.HasNext())
		{
			IHasWorldRectTile tile =  iter.Next();

			if(tile != null)
			{			
				if(IsPicking(tile.WorldRect, touchWorldPos) == true)
				{
					touchTilePos = tile._pos;
					return true;
				}
			}
		}
		
		return false;
	}
			
	/* x-z 좌표일때 타일의 제일 상단 마름모꼴의 월드좌표(좌측 상단)와 터치 위치의 월드 좌표를 받고 picking이 되었는지 확인하는 함수 */
	private bool IsPicking (Rect tileWorldRect, Vector3 touchWorldPos)
	{
		/* 마름모의 왼쪽 삼각형 (위중앙 왼쪽 아래중앙 점)*/
		Vector3 v1 = new Vector3 (tileWorldRect.x + (tileWorldRect.width/2), tileWorldRect.y, 0f);
		Vector3 v2 = new Vector3 (tileWorldRect.x, tileWorldRect.y - (tileWorldRect.height/2), 0f);
		Vector3 v3 = new Vector3 (tileWorldRect.x + (tileWorldRect.width/2), tileWorldRect.y - tileWorldRect.height, 0f);
		/* 마름모의 오른쪽 삼각형의 제일 오른쪽 점 */
		Vector3 v4 = new Vector3 (tileWorldRect.x + tileWorldRect.width, tileWorldRect.y - (tileWorldRect.height/2), 0f);

		Vector2 tocuchWorldPoint = new Vector2(touchWorldPos.x, touchWorldPos.z);
		
		if(MchUtil.Instance.IsPointInTri(v1, v2, v3, tocuchWorldPoint) == true)
		{
			return true;
		}
		
		if(MchUtil.Instance.IsPointInTri(v1, v3, v4, tocuchWorldPoint) == true)
		{
			return true;
		}
		
		return false;
	}


    

}

/* 맵 이터레이터 샘플 */
/*
public class BackwardTilesIterator : IBackwardIterator
{
	SpotMap _map;
	int _i;
	int _j;
	int _k;
	
	int _changeVar = 0;
	
	
	public BackwardTilesIterator(SpotMap map)
	{
		_map = map;
		_i = _map._width-1;
		_j = _map._length-1;
		_k = _map._height;
	}
	
	public bool HasNext()
	{
		if(_i==0 && _j==0 && _k==0)
		{
			return false;
		}
		
		return true;
	}
	
	public IHasWorldRectTile Next()
	{
		bool bI;
		bool bJ;
		
		bool bK = DecrementValue(ref _k);
		if(bK == false)
		{
			if(_i != 0 && _j != 0)
			{
				_k = _map._height;
			}
			
			bJ = DecrementValue(ref _j);
			if(bJ == false)
			{
				if(_i != 0)
				{
					_j = _map._length;
				}
				
				bI = DecrementValue(ref _i);
				if(bI == false)
				{
					_i = _j = _k = 0;
				}
			}
		}
		IHasWorldRectTile rtnTile = _map._tilesArray[_i, _j, _k];
		
		return rtnTile;
	}
	
	bool DecrementValue(ref int start)
	{
		if( start >= 0)
		{
			start--;
			return true;
		}
		else
		{
			return false;
		}
	}
}
*/
