using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Animations;

#if UNITY_2019_3_OR_NEWER
using UnityEngine.Animations;
#else
using UnityEngine.Experimental.Animations;
#endif

namespace Unity.Animations.SpringBones.Jobs
{
    public class SpringBoneJobManager : MonoBehaviour
    {
        [Header("Properties")] public bool automaticUpdates = true;
        public bool isPaused = false;
        public int simulationFrameRate = 60;
        [Range(0f, 1f)] public float dynamicRatio = 0.5f;
        public Vector3 gravity = new Vector3(0f, -10f, 0f);
        [Range(0f, 1f)] public float bounce = 0f;
        [Range(0f, 1f)] public float friction = 1f;

        [Header("Constraints")] public bool enableAngleLimits = true;
        public bool enableCollision = true;
        public bool enableLengthLimits = true;

        [Header("Ground Collision")] public bool collideWithGround = true;
        public float groundHeight = 0f;

        PlayableGraph m_Graph;
        public NativeArray<TransformStreamHandle> m_springBoneTransformHandles;
        NativeArray<SpringBoneProperties> m_SpringBoneProperties;
        NativeArray<SpringBoneComponent> m_springBoneComponents;

        private PlayableGraph InitializeGraph()
        {
            var springBones = FindSpringBones(true);
            var animator = GetComponent<Animator>();

            var nSpringBones = springBones.Length;
            m_springBoneTransformHandles = new NativeArray<TransformStreamHandle>(nSpringBones, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            
            m_SpringBoneProperties = new NativeArray<SpringBoneProperties>(nSpringBones, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            
            m_springBoneComponents = new NativeArray<SpringBoneComponent>(nSpringBones, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            
            for (var i = 0; i < nSpringBones; ++i)
            {
                m_springBoneTransformHandles[i] = animator.BindStreamTransform(springBones[i].transform);
            }
            
            // Create job.
            var springBoneJob = new SpringBoneJob
            {
                rootHandle = animator.BindStreamTransform(transform),
                springBoneTransformHandles = m_springBoneTransformHandles,
                springBoneProperties = m_SpringBoneProperties,
                springBoneComponents = m_springBoneComponents,
                isPaused = isPaused,
                simulationFrameRate = simulationFrameRate,
                dynamicRatio = dynamicRatio,
                gravity = gravity,
                bounce = bounce,
                friction = friction,
                enableAngleLimits = enableAngleLimits,
                enableCollision = enableCollision,
                enableLengthLimits = enableLengthLimits,
                collideWithGround = collideWithGround,
                groundHeight = groundHeight,
            };
            
            InitializeJobData(ref springBoneJob, springBones);

            // Create graph.
            var graph = PlayableGraph.Create("SpringBone");
            graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);

            var springBonePlayable = AnimationScriptPlayable.Create(graph, springBoneJob);
            var output = AnimationPlayableOutput.Create(graph, "SpringBoneOutput", animator);
            output.SetSourcePlayable(springBonePlayable);

            return graph;
        }
        
        private void InitializeJobData(ref SpringBoneJob job, SpringBone[] springBones)
        {
            for (var i = 0; i < springBones.Length; ++i)
            {
                InitializeSpringBoneComponent(i, springBones[i]);
            }
        }
        
        // This should be called by the SpringManager in its Awake function before any updates
        private void InitializeSpringBoneComponent(int index, SpringBone springBone)
        {
            var childPosition = ComputeChildBonePosition(springBone);
            var localChildPosition = springBone.transform.InverseTransformPoint(childPosition);
            var boneAxis = localChildPosition.normalized;

            var initialLocalRotation = springBone.transform.localRotation;
            var actualLocalRotation = initialLocalRotation;
            
            var springLength = Vector3.Distance(springBone.transform.position, childPosition);
            var currTipPos = childPosition;
            var prevTipPos = childPosition;

            m_SpringBoneProperties[index] = new SpringBoneProperties
            {
                stiffnessForce = springBone.stiffnessForce,
                dragForce =  springBone.dragForce,
                springForce = springBone.springForce,
                windInfluence = springBone.windInfluence,
                angularStiffness = springBone.angularStiffness,
                yAngleLimits =  new AngleLimitComponent
                {
                    active = springBone.yAngleLimits.active,
                    min = springBone.yAngleLimits.min,
                    max = springBone.yAngleLimits.max,
                },
                zAngleLimits = new AngleLimitComponent
                {
                    active = springBone.zAngleLimits.active,
                    min = springBone.zAngleLimits.min,
                    max = springBone.zAngleLimits.max,
                },
                radius = springBone.radius,
                boneAxis = boneAxis,
                springLength = springLength,
                isRootTransform = transform.parent == null
            };
            
            m_springBoneComponents[index] = new SpringBoneComponent
            {
                skinAnimationLocalRotation = initialLocalRotation, //temporal
                initialLocalRotation = initialLocalRotation,
                actualLocalRotation = actualLocalRotation,
                currentTipPosition = currTipPos,
                previousTipPosition = prevTipPos
            };
            
            // turn off SpringBone component to let Job work
            springBone.enabled = false; 
        }
        
        private static Vector3 ComputeChildBonePosition(SpringBone bone)
        {
            var children = GetValidSpringBoneChildren(bone.transform);
            var childCount = children.Count;

            if (childCount == 0)
            {
                // This should never happen
                Debug.LogWarning("SpringBone「" + bone.name + "」に有効な子供がありません");
                return bone.transform.position + bone.transform.right * -0.1f;
            }

            if (childCount == 1)
            {
                return children[0].position;
            }

            var initialTailPosition = new Vector3(0f, 0f, 0f);
            var averageDistance = 0f;
            var selfPosition = bone.transform.position;
            for (int childIndex = 0; childIndex < childCount; childIndex++)
            {
                var childPosition = children[childIndex].position;
                initialTailPosition += childPosition;
                averageDistance += (childPosition - selfPosition).magnitude;
            }

            averageDistance /= childCount;
            initialTailPosition /= childCount;
            var selfToInitial = initialTailPosition - selfPosition;
            selfToInitial.Normalize();
            initialTailPosition = selfPosition + averageDistance * selfToInitial;
            return initialTailPosition;
        }
        
        private static IList<Transform> GetValidSpringBoneChildren(Transform parent)
        {
            // Ignore SpringBonePivots
            var childCount = parent.childCount;
            var children = new List<Transform>(childCount);
            for (int childIndex = 0; childIndex < childCount; childIndex++)
            {
                var child = parent.GetChild(childIndex);
                if (child.GetComponent<SpringBonePivot>() == null)
                {
                    children.Add(child);
                }
            }

            return children;
        }
        
        
        // Find SpringBones in children and assign them in depth order.
        // Note that the original list will be overwritten.
        private SpringBone[] FindSpringBones(bool includeInactive = false)
        {
            var unsortedSpringBones = GetComponentsInChildren<SpringBone>(includeInactive);
            var boneDepthList = unsortedSpringBones
                .Select(bone => new {bone, depth = GetObjectDepth(bone.transform)})
                .ToList();
            boneDepthList.Sort((a, b) => a.depth.CompareTo(b.depth));
            return boneDepthList.Select(item => item.bone).ToArray();
        }

        // Get the depth of an object (number of consecutive parents)
        private static int GetObjectDepth(Transform inObject)
        {
            var depth = 0;
            var currentObject = inObject;
            while (currentObject != null)
            {
                currentObject = currentObject.parent;
                ++depth;
            }

            return depth;
        }        
        
        private void FinalizeGraph()
        {
            m_springBoneTransformHandles.Dispose();
            m_SpringBoneProperties.Dispose();
            m_springBoneComponents.Dispose();
            
            m_Graph.Destroy();
        }

        void OnEnable()
        {
            m_Graph = InitializeGraph();
            m_Graph.Play();
        }

//        void LateUpdate()
//        {
//            if (!m_Graph.IsValid())
//            {
//                return;
//            }
//        }

        void OnDisable()
        {
            if (!m_Graph.IsValid())
            {
                return;
            }

            FinalizeGraph();
        }
        

//        private void Start()
//        {
//            // Must get the ForceProviders in Start and not Awake or Unity will complain that
//            // "the scene is not loaded"
//            forceProviders = GameObjectUtil.FindComponentsOfType<ForceProvider>().ToArray();
//        }
    }
}