using UnityEngine;

[ExecuteAlways]
public class EditorUpDownAnimation : MonoBehaviour
{
    [Header("Editor Animation")]
    public bool animate = true;

    [Header("Movement Settings")]
    public float amplitude = 0.5f;
    public float speed = 2f;
    public bool useLocalPosition = true;

    private Vector3 basePosition;

    void OnEnable()
    {
        basePosition = GetCurrentPosition();
    }

    void Update()
    {
        if (!animate)
        {
            basePosition = GetCurrentPosition();
            return;
        }

        float time;

#if UNITY_EDITOR
        time = Application.isPlaying
            ? Time.time
            : (float)UnityEditor.EditorApplication.timeSinceStartup;
#else
        time = Time.time;
#endif

        float offsetY = Mathf.Sin(time * speed) * amplitude;

        SetCurrentPosition(basePosition + new Vector3(0f, offsetY, 0f));
    }

    Vector3 GetCurrentPosition()
    {
        return useLocalPosition ? transform.localPosition : transform.position;
    }

    void SetCurrentPosition(Vector3 position)
    {
        if (useLocalPosition)
            transform.localPosition = position;
        else
            transform.position = position;
    }
}