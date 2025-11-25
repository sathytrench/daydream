using UnityEngine;

[RequireComponent(typeof(Collider))]
public class DoorTrigger : MonoBehaviour
{
    public HallwayManager hallwayManager;
    [Range(0, 1)]
    public int doorIndex = 0;  // 0 = first linked title, 1 = second
    public string playerTag = "Player";

    private void Reset()
    {
        var col = GetComponent<Collider>();
        if (col) col.isTrigger = true;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag(playerTag) && hallwayManager != null)
        {
            hallwayManager.RespawnAndGoToDoor(doorIndex);
        }
    }
}
