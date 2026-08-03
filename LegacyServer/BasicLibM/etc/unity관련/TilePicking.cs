/* 실사용 예 */

/*
	
	TilePicking _tilePick = new TilePicking(MyMap);

	void Update () {

		if (Input.GetMouseButtonDown(0))	
		{
			Vector3 touchWorldPos = _mainCamera.camera.ScreenToWorldPoint(Input.mousePosition);
			Vector3 touchTilePos = new Vector3(-1f, -1f, -1f);
			if(_tilePick.PickingTilePos(touchWorldPos, out touchTilePos) == true)
			{
				Debug.Log("Tile touch Pos: " + touchTilePos.ToString());
			}
		}	
	}
*/


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
    
    
    /* 타일의 제일 상단 마름모꼴의 월드좌표(좌측 상단)와 터치 위치의 월드 좌표를 받고 picking이 되었는지 확인하는 함수 */
	private bool IsPicking (Rect tileWorldRect, Vector3 touchWorldPos)
	{
		/* 마름모의 왼쪽 삼각형 (위중앙 왼쪽 아래중앙 점)*/
		Vector3 v1 = new Vector3 (tileWorldRect.x + (tileWorldRect.width/2), tileWorldRect.y, 0f);
		Vector3 v2 = new Vector3 (tileWorldRect.x, tileWorldRect.y - (tileWorldRect.height/2), 0f);
		Vector3 v3 = new Vector3 (tileWorldRect.x + (tileWorldRect.width/2), tileWorldRect.y - tileWorldRect.height, 0f);
		/* 마름모의 오른쪽 삼각형의 제일 오른쪽 점 */
		Vector3 v4 = new Vector3 (tileWorldRect.x + tileWorldRect.width, tileWorldRect.y - (tileWorldRect.height/2), 0f);
		
		if(MchUtil.Instance.IsPointInTri(v1, v2, v3, touchWorldPos) == true)
		{
			return true;
		}
		
		if(MchUtil.Instance.IsPointInTri(v1, v3, v4, touchWorldPos) == true)
		{
			return true;
		}
		
		return false;
	}	
    
    
    /* Barycentric 알고리즘을 써서 세점안에 한점이 있는지 검사하는 함수 - made by 강명훈 */
    private bool IsPointInTri(Vector3 v1, Vector3 v2, Vector3 v3, Vector3 point)
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


/* 맵 이터레이터 샘플 - 구상 클래스 이므로 실제 맵인 SpotMap을 받았음 */
/* 실제 사용시에는 자신이 구현한 맵 데이터를 가지고 Iterator를 구현하면 됨 
public class BackwardTilesIterator : IBackwardIterator
{
	SpotMap _map;
	int _i;
	int _j;
	int _k;

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
				_k = _map._height-1;
			}

			bJ = DecrementValue(ref _j);
			if(bJ == false)
			{
				if(_i != 0)
				{
					_j = _map._length-1;
				}

				bI = DecrementValue(ref _i);
			}
		}
		IHasWorldRectTile rtnTile = _map._tilesArray[_i, _j, _k];
		return rtnTile;
	}

	bool DecrementValue(ref int start)
	{	
		if( start > 0)
		{
			start --;
			return true;
		}
		else
		{
			return false;
		}
	}
}
*/
