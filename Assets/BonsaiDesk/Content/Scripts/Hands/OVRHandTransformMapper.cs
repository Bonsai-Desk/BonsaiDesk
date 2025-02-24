﻿using System;
using System.Collections.Generic;
using UnityEngine;

public class OVRHandTransformMapper : MonoBehaviour
{
    public OVRSkeleton.SkeletonType _skeletonType = OVRSkeleton.SkeletonType.None;

    [SerializeField]
    private List<Transform> _customBones = new List<Transform>(new Transform[(int) OVRSkeleton.BoneId.Max]);

    [SerializeField]
    private List<Transform> _boneTargets = new List<Transform>(new Transform[(int) OVRSkeleton.BoneId.Max]);

    public bool useLocalRotation = true;

    public Transform targetObject;
    public bool moveObjectToTarget = true;

    public Transform capsulesParent;
    public bool moveBonesToTargets = true;

    private readonly Quaternion _fixRotation = Quaternion.AngleAxis(180f, Vector3.up);

    private Quaternion[] _startPose = new Quaternion[24];

    public bool fixRotation = true;

    private void Start()
    {
        for (int i = 0; i < CustomBones.Count && i < _startPose.Length; i++)
        {
            _startPose[i] = CustomBones[i].localRotation;
        }
    }

    private void Update()
    {
        if (moveObjectToTarget)
        {
            UpdateObjectToTarget();
        }

        if (moveBonesToTargets)
        {
            UpdateBonesToTargets();
        }
    }

    public void UpdateObjectToTarget()
    {
        if (!targetObject)
            return;

        transform.position = targetObject.position;
        if (fixRotation)
        {
            transform.rotation = targetObject.rotation * _fixRotation;
        }
        else
        {
            transform.rotation = targetObject.rotation;
        }
    }

    public void UpdateBonesToTargets()
    {
        if (CustomBones.Count == BoneTargets.Count)
        {
            for (int i = 0; i < CustomBones.Count; i++)
            {
                if (CustomBones[i] != null && BoneTargets[i] != null)
                {
                    if (useLocalRotation)
                    {
                        CustomBones[i].localRotation = BoneTargets[i].localRotation;
                    }
                    else
                    {
                        CustomBones[i].position = BoneTargets[i].position;
                        CustomBones[i].rotation = BoneTargets[i].rotation;
                    }
                }
            }
        }
        else
        {
            Debug.LogError("BoneTargets length must equal Bones length");
        }
    }

    public void UpdateBonesToStartPose()
    {
        UpdateBonesToPose(_startPose);
    }

    private void UpdateBonesToPose(Quaternion[] pose)
    {
        for (int i = 0; i < pose.Length; i++)
        {
            if (pose[i] != null && CustomBones[i])
            {
                CustomBones[i].localRotation = pose[i];
            }
        }
    }

    private static readonly string[] _fbxBoneNames =
    {
        "wrist",
        "forearm_stub",
        "thumb0",
        "thumb1",
        "thumb2",
        "thumb3",
        "index1",
        "index2",
        "index3",
        "middle1",
        "middle2",
        "middle3",
        "ring1",
        "ring2",
        "ring3",
        "pinky0",
        "pinky1",
        "pinky2",
        "pinky3"
    };

    private static readonly string[] _fbxFingerNames =
    {
        "thumb",
        "index",
        "middle",
        "ring",
        "pinky"
    };

    private static readonly string[] _handPrefix = {"l_", "r_"};

    public List<Transform> CustomBones
    {
        get { return _customBones; }
    }

    public List<Transform> BoneTargets
    {
        get { return _boneTargets; }
    }

    public void TryAutoMapBoneTargets()
    {
        TryAutoMapBoneTargets(capsulesParent, "_CapsuleRigidBody");
    }

    public void TryAutoMapBoneTargets(Transform transformToCheck, string suffix)
    {
        OVRSkeleton.BoneId start = OVRSkeleton.BoneId.Hand_Start;
        OVRSkeleton.BoneId end = OVRSkeleton.BoneId.Hand_End;
        if (start != OVRSkeleton.BoneId.Invalid && end != OVRSkeleton.BoneId.Invalid)
        {
            for (int bi = (int) start; bi < (int) end; ++bi)
            {
                if (!((OVRSkeleton.BoneId) bi >= OVRSkeleton.BoneId.Hand_ThumbTip && (OVRSkeleton.BoneId) bi <= OVRSkeleton.BoneId.Hand_PinkyTip))
                {
                    string fbxBoneName = _fbxBoneNames[(int) bi];
                    // print(fbxBoneName);

                    if (int.TryParse(fbxBoneName.Substring(fbxBoneName.Length - 1), out int index))
                    {
                        if (index >= 0)
                        {
                            fbxBoneName = char.ToUpper(fbxBoneName[0]) + fbxBoneName.Substring(1);
                            fbxBoneName = "Hand_" + fbxBoneName + suffix;

                            // print(fbxBoneName);

                            Transform t = transformToCheck.FindChildRecursive(fbxBoneName);
                            if (t != null)
                            {
                                _boneTargets[(int) bi] = t;
                            }
                        }
                    }
                }
            }
        }
    }

    public void TryAutoMapBoneTargetsAPIHand()
    {
        TryAutoMapBoneTargets(targetObject, "");
    }

    public void TryAutoMapBonesByName()
    {
        TryAutoMapBonesByName(transform, _customBones);
    }

    public void TryAutoMapBonesTargetsByName()
    {
        TryAutoMapBonesByName(capsulesParent, _boneTargets);
    }

    public void TryAutoMapBonesByName(Transform parent, List<Transform> target)
    {
        OVRSkeleton.BoneId start = OVRSkeleton.BoneId.Hand_Start;
        OVRSkeleton.BoneId end = OVRSkeleton.BoneId.Hand_End;
        if (start != OVRSkeleton.BoneId.Invalid && end != OVRSkeleton.BoneId.Invalid)
        {
            for (int bi = (int) start; bi < (int) end; ++bi)
            {
                string fbxBoneName = FbxBoneNameFromBoneId(_skeletonType, (OVRSkeleton.BoneId) bi);
                var t = parent.FindChildRecursive(fbxBoneName);

                if (t != null)
                {
                    target[(int) bi] = t;
                }
            }
        }
    }

    public void ResetBoneTargets()
    {
        for (int i = 0; i < _boneTargets.Count; i++)
            _boneTargets[i] = null;
    }

    private static string FbxBoneNameFromBoneId(OVRSkeleton.SkeletonType skeletonType, OVRSkeleton.BoneId bi)
    {
        if (bi >= OVRSkeleton.BoneId.Hand_ThumbTip && bi <= OVRSkeleton.BoneId.Hand_PinkyTip)
        {
            return _handPrefix[(int) skeletonType] + _fbxFingerNames[(int) bi - (int) OVRSkeleton.BoneId.Hand_ThumbTip] + "_finger_tip_marker";
        }
        else
        {
            return "b_" + _handPrefix[(int) skeletonType] + _fbxBoneNames[(int) bi];
        }
    }
}