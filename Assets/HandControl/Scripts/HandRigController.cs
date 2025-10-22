using UnityEngine;

namespace HandControl
{
  [RequireComponent(typeof(Animator))]
  public class HandRigController : MonoBehaviour
  {
    [SerializeField] private HandTrackingSource source;
    [SerializeField] private Animator animator;

    [Header("Bone Mapping")]
    [SerializeField] private HumanBodyBones leftArmBone = HumanBodyBones.LeftUpperArm;
    [SerializeField] private HumanBodyBones rightArmBone = HumanBodyBones.RightUpperArm;
    [SerializeField] private HumanBodyBones headBone = HumanBodyBones.Head;
    [SerializeField] private bool useRightHand = true;

    [Header("Rotation Settings")]
    [SerializeField] private Vector3 leftArmAxis = Vector3.forward;
    [SerializeField] private Vector3 rightArmAxis = Vector3.forward;
    [SerializeField] private Vector3 headAxis = Vector3.right;
    [SerializeField] private float leftArmMaxAngle = 70f;
    [SerializeField] private float rightArmMaxAngle = 70f;
    [SerializeField] private float headMaxAngle = 30f;
    [SerializeField] private float trioActivationThreshold = 0.2f;
    [SerializeField] private float fingerGain = 3f;
    [SerializeField] private float smoothing = 10f;

    private Transform _leftArmTransform;
    private Transform _rightArmTransform;
    private Transform _headTransform;

    private Quaternion _leftArmRest = Quaternion.identity;
    private Quaternion _rightArmRest = Quaternion.identity;
    private Quaternion _headRest = Quaternion.identity;

    private float _thumbWeight;
    private float _indexWeight;
    private float _trioWeight;

    private void Reset()
    {
      animator = GetComponent<Animator>();
    }

    private void Awake()
    {
      if (animator == null)
      {
        animator = GetComponent<Animator>();
      }
      CacheBones();
    }

    private void OnEnable()
    {
      if (source != null)
      {
        source.OnHandFrame += HandleHandFrame;
      }
    }

    private void OnDisable()
    {
      if (source != null)
      {
        source.OnHandFrame -= HandleHandFrame;
      }
    }

    private void CacheBones()
    {
      if (animator == null || !animator.isHuman)
      {
        Debug.LogWarning("HandRigController: Animator is missing or not humanoid.");
        return;
      }

      _leftArmTransform = animator.GetBoneTransform(leftArmBone);
      _rightArmTransform = animator.GetBoneTransform(rightArmBone);
      _headTransform = animator.GetBoneTransform(headBone);

      if (_leftArmTransform != null)
      {
        _leftArmRest = _leftArmTransform.localRotation;
      }

      if (_rightArmTransform != null)
      {
        _rightArmRest = _rightArmTransform.localRotation;
      }

      if (_headTransform != null)
      {
        _headRest = _headTransform.localRotation;
      }
    }

    private void HandleHandFrame(HandTrackingSource.HandFrameData frame)
    {
      if (!frame.tracked || frame.landmarks == null || frame.landmarks.Length < 21)
      {
        RelaxTowardsRest();
        ApplyRotations();
        return;
      }

      if (useRightHand && !frame.isRight)
      {
        RelaxTowardsRest();
        ApplyRotations();
        return;
      }

      if (!useRightHand && frame.isRight)
      {
        RelaxTowardsRest();
        ApplyRotations();
        return;
      }

      var wrist = frame.landmarks[0];
      var thumbTip = frame.landmarks[4];
      var indexTip = frame.landmarks[8];
      var middleTip = frame.landmarks[12];
      var ringTip = frame.landmarks[16];
      var pinkyTip = frame.landmarks[20];

      var thumbTarget = ComputeFingerWeight(wrist.y, thumbTip.y);
      var indexTarget = ComputeFingerWeight(wrist.y, indexTip.y);
      var middleWeight = ComputeFingerWeight(wrist.y, middleTip.y);
      var ringWeight = ComputeFingerWeight(wrist.y, ringTip.y);
      var pinkyWeight = ComputeFingerWeight(wrist.y, pinkyTip.y);

      var trioTarget = (middleWeight + ringWeight + pinkyWeight) / 3f;
      trioTarget = trioTarget > trioActivationThreshold ? trioTarget : 0f;

      var lerpSpeed = Time.deltaTime * Mathf.Max(0f, smoothing);
      _thumbWeight = Mathf.Lerp(_thumbWeight, thumbTarget, lerpSpeed);
      _indexWeight = Mathf.Lerp(_indexWeight, indexTarget, lerpSpeed);
      _trioWeight = Mathf.Lerp(_trioWeight, trioTarget, lerpSpeed);

      ApplyRotations();
    }

    private float ComputeFingerWeight(float wristY, float tipY)
    {
      return Mathf.Clamp01((wristY - tipY) * fingerGain);
    }

    private void RelaxTowardsRest()
    {
      var lerpSpeed = Time.deltaTime * Mathf.Max(0f, smoothing);
      _thumbWeight = Mathf.Lerp(_thumbWeight, 0f, lerpSpeed);
      _indexWeight = Mathf.Lerp(_indexWeight, 0f, lerpSpeed);
      _trioWeight = Mathf.Lerp(_trioWeight, 0f, lerpSpeed);
    }

    private void ApplyRotations()
    {
      if (_leftArmTransform != null)
      {
        _leftArmTransform.localRotation = ApplyAxis(_leftArmRest, leftArmAxis, _thumbWeight * leftArmMaxAngle);
      }

      if (_rightArmTransform != null)
      {
        _rightArmTransform.localRotation = ApplyAxis(_rightArmRest, rightArmAxis, _trioWeight * rightArmMaxAngle);
      }

      if (_headTransform != null)
      {
        _headTransform.localRotation = ApplyAxis(_headRest, headAxis, _indexWeight * headMaxAngle);
      }
    }

    private static Quaternion ApplyAxis(Quaternion rest, Vector3 axis, float angle)
    {
      if (axis.sqrMagnitude < 1e-4f || Mathf.Approximately(angle, 0f))
      {
        return rest;
      }

      return rest * Quaternion.AngleAxis(angle, axis.normalized);
    }
  }
}
