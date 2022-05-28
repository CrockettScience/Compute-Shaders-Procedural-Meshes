using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static Unity.Mathematics.math;
using float2 = Unity.Mathematics.float2;
using float3 = Unity.Mathematics.float3;


public class BezierMeshRenderer : MonoBehaviour
{
    //Todo
    //Batching?
    //Calculate better mesh bounds
    //Proper recieve shadows (Maybe it's a shader keyword?)
    //Generate lightmap UVs for lighting and tangents for normal maps
    //Transparent & Unlit Shaders
    
    [SerializeField] private bool keepSelectedGizmoOn;
    
    [Space]
    [SerializeField] private int curveSegmentCount;
    [SerializeField] private MeshRotationMode rotationMode;
    [SerializeField] private float bankingAngle;
    [SerializeField] private bool frameCorrection;

    [Space]
    [SerializeField] private Transform startPoint;
    [SerializeField] private float startLength;
    [SerializeField] private bool startPointMeshCap = true;
    
    [Space]
    [SerializeField] private Transform endPoint;
    [SerializeField] private float endLength;
    [SerializeField] private bool endPointMeshCap = true;
    
    [Space]
    [SerializeField] private Mesh meshSegment;
    [SerializeField] private float sourceMeshSegmentSize = 1;
    [SerializeField] public Material material;
    [SerializeField] public ShadowCastingMode shadowCastingMode;
    [SerializeField] public bool recieveShadows;
    
    //State Info
    private float _currentStartLength;
    private float _currentEndLength;
    private int _currentSegmentCount;
    private bool _currentFrameCorrection;
    private Mesh _currentMesh;
    private float _currentSourceMeshSegmentSize;
    private bool _currentRecieveShadows;
    private bool _currentStartPointMeshCap;
    private bool _currentEndPointMeshCap;
    private Vector3 _currentStartPosition;
    private Vector3 _currentEndPosition;
    private Quaternion _currentStartRotation;
    private Quaternion _currentEndRotation;

    //Shaders
    private ComputeShader _bezierCompute;
    private int _kernelComputeCurveFrames;

    //Buffers: Compute
    private ComputeBuffer _tVectorsBuffer;
    
    //Buffers: Surface
    private ComputeBuffer _srcMeshBuffer;
    private ComputeBuffer _bezierMeshIndex;
    
    //Buffers: Shared
    private ComputeBuffer _curveFramesBuffer;

    //Shader Property ID's
    private static readonly int CURVE_FRAMES_PID = Shader.PropertyToID("_CurveFrames");
    private static readonly int TVECTORS_PID = Shader.PropertyToID("_TVectors");
    private static readonly int CONTROL_POINTS_PID = Shader.PropertyToID("_ControlPointsMatrix");
    private static readonly int CONTROL_QUATERNIONS_PID = Shader.PropertyToID("_ControlQuaternionsMatrix");
    private static readonly int SRC_MESH_PID = Shader.PropertyToID("_SrcMesh");
    private static readonly int MESH_INDEX_PID = Shader.PropertyToID("_BMeshIndex");
    private static readonly int FRAME_CORRECTION_PID = Shader.PropertyToID("_FrameCorrection");
    private static readonly int RECIEVE_SHADOWS_PID = Shader.PropertyToID("_RecieveShadows");
    
    

    private void Start()
    {
        _bezierCompute = Instantiate(Resources.Load<ComputeShader>("BezierCompute"));
        _kernelComputeCurveFrames = _bezierCompute.FindKernel("ComputeCurveFrames");

        var instance = Instantiate(material);
        instance.name = material.name + " (Instance)";
        material = instance;
        material.SetFloat(RECIEVE_SHADOWS_PID, recieveShadows ? 1 : 0);
    }

    private void OnValidate()
    {
        if (curveSegmentCount <= 0)
        {
            curveSegmentCount = 1;
        }
    }

    private void Update()
    {
        var p0 = startPoint.position;
        var p3 = endPoint.position;
    
        var p1 = p0 + startPoint.forward * startLength;
        var p2 = p3 - endPoint.forward * endLength;
        
        //Validate Mesh Data
        if (_currentMesh != meshSegment || 
            _currentSegmentCount != curveSegmentCount || 
            Math.Abs(_currentSourceMeshSegmentSize - sourceMeshSegmentSize) > 0.0001f ||
            _currentStartPointMeshCap != startPointMeshCap ||
            _currentEndPointMeshCap != endPointMeshCap)
        {
            RefreshMesh();
            
            //Update State info
            _currentMesh = meshSegment;
            _currentSourceMeshSegmentSize = sourceMeshSegmentSize;
            _currentStartPointMeshCap = startPointMeshCap;
            _currentEndPointMeshCap = endPointMeshCap;
        }

        if (_currentRecieveShadows != recieveShadows)
        {
            //For some reason, I can't get DrawProcedural() "receiveShadows" argument to work properly, so here's my work around for now
            material.SetFloat(RECIEVE_SHADOWS_PID, recieveShadows ? 1 : 0);
            
            //Update State info
            _currentRecieveShadows = recieveShadows;
        }
        
        var refreshCurve = false;
        
        //Validate control points
        if (_currentStartPosition != startPoint.position || 
            _currentEndPosition != endPoint.position || 
            _currentStartRotation != startPoint.rotation ||
            _currentEndRotation != endPoint.rotation ||
            Math.Abs(_currentEndLength - endLength) > 0.01f || 
            Math.Abs(_currentStartLength - startLength) > 0.01f)
        {
            RefreshControlPoints(p0, p1, p2, p3);
            
            //Update State info
            startPoint.hasChanged = false;
            endPoint.hasChanged = false;
            _currentStartLength = startLength;
            _currentEndLength = endLength;
            _currentStartPosition = startPoint.position;
            _currentEndPosition = endPoint.position;
            _currentStartRotation = startPoint.rotation;
            _currentEndRotation = endPoint.rotation;
            
            //Set to compute curve
            refreshCurve = true;
        }
        
        //Validate TVectors
        if (_currentSegmentCount != curveSegmentCount)
        {
            RefreshTVectors(BuildTValues(curveSegmentCount));
            
            //Update State info
            _currentSegmentCount = curveSegmentCount;
            
            //Set to compute curve
            refreshCurve = true;
        }

        //Validate Other
        if (_currentFrameCorrection != frameCorrection)
        {
            _bezierCompute.SetFloat(FRAME_CORRECTION_PID, frameCorrection ? 1f : 0f);
            
            
            //Update State info
            _currentFrameCorrection = frameCorrection;
            
            //Set to compute curve
            refreshCurve = true;
        }

        if (refreshCurve)
        {
            //Compute Curve
            _bezierCompute.Dispatch(_kernelComputeCurveFrames, (curveSegmentCount + 1) / 32 + 1, 1, 1);
        }

        //Draw
        Graphics.DrawProcedural(material, BuildBezierAABB(p0, p1, p2, p3), MeshTopology.Triangles,_bezierMeshIndex.count, 1, null, null, shadowCastingMode);
    }

    private void OnDestroy()
    {
        _curveFramesBuffer?.Dispose();
        _tVectorsBuffer?.Dispose();
        _srcMeshBuffer?.Dispose();
        _bezierMeshIndex?.Dispose();
    }

    private void RefreshTVectors(Vector4[] tVectors)
    {
        _curveFramesBuffer?.Dispose();
        _tVectorsBuffer?.Dispose();

        var length = tVectors.Length % 32 == 0 ? tVectors.Length : (tVectors.Length / 32 + 1) * 32;
        
        _curveFramesBuffer = new ComputeBuffer(length, sizeof(float) * 12);
        _tVectorsBuffer = new ComputeBuffer(length, sizeof(float) * 4);
        
        _tVectorsBuffer.SetData(tVectors);
        
        _bezierCompute.SetBuffer(_kernelComputeCurveFrames, TVECTORS_PID, _tVectorsBuffer);
        _bezierCompute.SetBuffer(_kernelComputeCurveFrames, CURVE_FRAMES_PID, _curveFramesBuffer);
        material.SetBuffer(CURVE_FRAMES_PID, _curveFramesBuffer);
    }
    
    private void RefreshControlPoints(float3 p0, float3 p1, float3 p2, float3 p3)
    {
        _bezierCompute.SetMatrix(CONTROL_POINTS_PID, BuildControlPoints(p0, p1, p2, p3));
        _bezierCompute.SetMatrix(CONTROL_QUATERNIONS_PID, BuildControlRotations(p0, p1, p2, p3));
    }

    private void RefreshMesh()
    {
        if (meshSegment)
        {
            //Dissect our mesh and sort the triangles
            var indexGroupA = new HashSet<int>();
            var indexGroupB = new HashSet<int>();
            var indexGroupC = new HashSet<int>();

            //Store the indices based on the designation given in UV2
            for (var i = 0; i < meshSegment.uv2.Length; i++)
            {
                var uv2 = meshSegment.uv2[i];
                if (uv2.x > 0.5f)
                    indexGroupB.Add(i);
                else
                {
                    if (uv2.y > 0.5f)
                        indexGroupC.Add(i);
                    else
                        indexGroupA.Add(i);
                }
            }
            
            //Build the source mesh and sort the triangles by the overlap in the vertex groups
            var triGroupA = new HashSet<Triangle>();
            var triGroupB = new HashSet<Triangle>();
            var triGroupC = new HashSet<Triangle>();
            

            for (var i = 0; i < meshSegment.triangles.Length; i += 3)
            {
                var tri = new Triangle();

                var inA = false;
                var inB = false;
                var inC = false;
                
                //First, sort the triangles based on overlap within the vertex groups
                for (var j = 0; j < 3; j++)
                {
                    var index = meshSegment.triangles[i + j];

                    if (indexGroupA.Contains(index))
                        inA = true;
                    else if (indexGroupB.Contains(index))
                        inB = true;
                    else if (indexGroupC.Contains(index))
                        inC = true;
                }

                //Use helper booleans to calculate frame offset, then save out verts
                var isStartTri = inA && inB && !inC || inA && !inB && !inC;
                var isEndTri = !inA && inB && inC || !inA && !inB && inC;
                var isMidTri = !inA && inB && !inC;
                
                for (var j = 0; j < 3; j++)
                {
                    
                    var index = meshSegment.triangles[i + j];
                    var uv2 = meshSegment.uv2[index] + Vector2.one * 0.5f;

                    var frameOffset = (int) (isStartTri ? uv2.x :
                                            isEndTri ? 1f - uv2.x :
                                            isMidTri ? uv2.y : 0);
                    
                    var segmentOffset = (isStartTri ? 0f :
                                              isEndTri ? 2f :
                                              isMidTri ? 1f : 0f) * sourceMeshSegmentSize;

                    var vert = new Vertex
                    {
                        PositionOS = meshSegment.vertices[index] - Vector3.forward * segmentOffset - Vector3.forward * frameOffset,
                        NormalOS = meshSegment.normals.Length > index ? (float3) meshSegment.normals[index] : float3.zero,
                        UV = meshSegment.uv.Length > index ? (float2) meshSegment.uv[index] : float2.zero,
                        Color = meshSegment.colors.Length > index ? meshSegment.colors[index] : Color.white,
                        FrameOffset = frameOffset
                    };

                    tri[j] = vert;
                }

                if (isMidTri)
                    triGroupB.Add(tri);
                else if (isStartTri)
                    triGroupA.Add(tri);
                else if (isEndTri)
                    triGroupC.Add(tri);
            }
            
            //build index buffer and src mesh for surface shader
            var bezierMeshIndex = new List<uint2>();
            var id = uint2(0, 0);
            
            //The mid segment(s)
            for (id.x = 1; id.x < curveSegmentCount; id.x++)
            {
                for (id.y = 0; id.y < triGroupB.Count; id.y++)
                {
                    bezierMeshIndex.Add(id);
                    bezierMeshIndex.Add(id);
                    bezierMeshIndex.Add(id);
                }
            }
            
            //The start segment
            if (startPointMeshCap)
            {
                for (id.x = 0; id.y < triGroupA.Count + triGroupB.Count; id.y++)
                {
                    bezierMeshIndex.Add(id);
                    bezierMeshIndex.Add(id);
                    bezierMeshIndex.Add(id);
                }
            }
            else
            {
                for (id.x = 0, id.y = 0; id.y < triGroupB.Count; id.y++)
                {
                    bezierMeshIndex.Add(id);
                    bezierMeshIndex.Add(id);
                    bezierMeshIndex.Add(id);
                }
            }

            //The end segment
            if (endPointMeshCap)
            {
                for (id.x = (uint) curveSegmentCount, id.y = (uint) (triGroupA.Count + triGroupB.Count); id.y < triGroupA.Count + triGroupB.Count + triGroupC.Count; id.y++)
                {
                    bezierMeshIndex.Add(id);
                    bezierMeshIndex.Add(id);
                    bezierMeshIndex.Add(id);
                }
            }

            var srcMesh = triGroupB.Concat(triGroupA).Concat(triGroupC).ToArray();

            _bezierMeshIndex?.Dispose();
            _srcMeshBuffer?.Dispose();

            _bezierMeshIndex = new ComputeBuffer(bezierMeshIndex.Count, sizeof(uint) * 2);
            _srcMeshBuffer = new ComputeBuffer(srcMesh.Length, (sizeof(float) * 12 + sizeof(int)) * 3);

            _srcMeshBuffer.SetData(srcMesh);
            _bezierMeshIndex.SetData(bezierMeshIndex.ToArray());
            
            material.SetBuffer(CURVE_FRAMES_PID, _curveFramesBuffer);
            material.SetBuffer(MESH_INDEX_PID, _bezierMeshIndex);
            material.SetBuffer(SRC_MESH_PID, _srcMeshBuffer);
        }

    }

    private Vector4[] BuildTValues(int count)
    {
        //Remember, we need 1 more tValue than the number of segments, otherwise we won't have room for our last endpoint.
        var tVals = new Vector4[count + 1];
        
        var tLength = 1.0f / count;
        
        for (var i = 0; i < tVals.Length; i++)
        {
            var t = tLength * i;
            tVals[i] = new Vector4(pow(t, 3), pow(t, 2), t, 1);
        }

        return tVals;
    }
    
    private float4x4 BuildControlPoints(float3 p0, float3 p1, float3 p2, float3 p3)
    {
        //Unity only marshals matrices with size 4x4
        return new float4x4(
            p0.x, p0.y, p0.z, 0,
            p1.x, p1.y, p1.z, 0,
            p2.x, p2.y, p2.z, 0,
            p3.x, p3.y, p3.z, 0
        );
    }

    private float4x4 BuildControlRotations(float3 p0, float3 p1, float3 p2, float3 p3)
    {
        Quaternion q0, q1, q2, q3;
        
        //Calculate a set of control rotations
        var startUp = startPoint.up;
        var endUp = endPoint.up;
        
        var banking = Quaternion.AngleAxis(bankingAngle, p2 - p1);

        switch (rotationMode)
        {
            case MeshRotationMode.flexible:
            {
                q0 = startPoint.rotation;
                q3 = endPoint.rotation;

                q1 = Quaternion.AngleAxis(bankingAngle, p1 - p0) * Quaternion.LookRotation(p1 - p0, Vector3.Lerp(startUp, endUp, 0.3333f));
                q2 = Quaternion.AngleAxis(bankingAngle, p2 - p1) * Quaternion.LookRotation(p2 - p1, Vector3.Lerp(startUp, endUp, 0.6666f));
                break;
            }

            case MeshRotationMode.alwaysUP:
            {
                q0 = Quaternion.LookRotation(normalize(startPoint.forward * float3(1, 0, 1)), Vector3.up);
                q3 = Quaternion.LookRotation(normalize(endPoint.forward * float3(1, 0, 1)), Vector3.up);

                q1 = banking * Quaternion.LookRotation(p1 - p0, Vector3.up);
                q2 = banking * Quaternion.LookRotation(p2 - p1, Vector3.up);
                break;
            }

            case MeshRotationMode.preferUp:
            {
                q0 = startPoint.rotation;
                q3 = endPoint.rotation;

                q1 = banking * Quaternion.LookRotation(p3 - p1, Vector3.up);
                q2 = banking * Quaternion.LookRotation(p3 - p2, Vector3.up);
                break;
            }

            default:
                throw new ArgumentOutOfRangeException();
        }
        
        return new float4x4(
            q0.x, q0.y, q0.z, q0.w,
            q1.x, q1.y, q1.z, q1.w,
            q2.x, q2.y, q2.z, q2.w,
            q3.x, q3.y, q3.z, q3.w
        );
    }
    
    private Bounds BuildBezierAABB(float3 p0, float3 p1, float3 p2, float3 p3)
    {
        return new Bounds((p0 + p1 + p2 + p3) / 4, new Vector3(
            max(max(p0.x, p1.x), max(p2.x, p3.x)) - min(min(p0.x, p1.x), min(p2.x, p3.x)),
            max(max(p0.y, p1.y), max(p2.y, p3.y)) - min(min(p0.y, p1.y), min(p2.y, p3.y)),
            max(max(p0.z, p1.z), max(p2.z, p3.z)) - min(min(p0.z, p1.z), min(p2.z, p3.z))));
    }

    private void OnDrawGizmos()
    {
        
        //Render endpoint icons if they are assigned
        if (startPoint & endPoint)
        {
            Gizmos.DrawIcon(startPoint.position, "Gizmo_BezierMesh.tiff", true);
            Gizmos.DrawIcon(endPoint.position, "Gizmo_BezierMesh.tiff", true);
        }
        //Otherwise, render a different icon to show that the points are not assigned
        else
        {
            Gizmos.DrawIcon(transform.position, "Gizmo_BezierMesh_Incomplete.tiff", true);
        }
        
        if(keepSelectedGizmoOn)
            DrawGizmosDetailed();
    }

    private void OnDrawGizmosSelected()
    {
        if(!keepSelectedGizmoOn)
            DrawGizmosDetailed();
    }

    private void DrawGizmosDetailed()
    {
        //Don't draw anything unless we actually have end points
        if (startPoint & endPoint)
        {
            //Draw icons for the derived control points
            var p0 = startPoint.position;
            var p3 = endPoint.position;
            
            var p1 = p0 + startPoint.forward * startLength;
            var p2 = p3 - endPoint.forward * endLength;
            
            Gizmos.DrawIcon(p0, "Gizmo_ControlPoint.tiff", true, Color.black);
            Gizmos.DrawIcon(p1, "Gizmo_ControlPoint.tiff", true, Color.black);
            Gizmos.DrawIcon(p2, "Gizmo_ControlPoint.tiff", true, Color.black);
            Gizmos.DrawIcon(p3, "Gizmo_ControlPoint.tiff", true, Color.black);

            //Connect the control points with lines
            Gizmos.color = Color.black;
            
            Gizmos.DrawLine(p0, p1);
            Gizmos.DrawLine(p1, p2);
            Gizmos.DrawLine(p2, p3);
            
            var curveFrames = new BezierFrame[curveSegmentCount + 1];
            
            //Check if one of the compute buffers exist; if so, use existing compute shader, else, instantiate and destroy new shader
            if (_curveFramesBuffer != null)
            {
                _curveFramesBuffer.GetData(curveFrames);
            }

            else
            {
                //Construct lines for each curve segment using a compute shader
                var shaderBezierCompute = Resources.Load<ComputeShader>("BezierCompute");
                
                var controlPointsMatrixCompute = BuildControlPoints(p0, p1, p2, p3);
                var controlQuaternionsMatrix = BuildControlRotations(p0, p1, p2, p3);
                var tVectors = BuildTValues(curveSegmentCount);
                
                //1) Find the Kernel
                var shaderKernelComputeCurveFrames = shaderBezierCompute.FindKernel("ComputeCurveFrames");

                //2) Create our buffers
                var length = tVectors.Length % 32 == 0 ? tVectors.Length : (tVectors.Length / 32 + 1) * tVectors.Length;
                
                var curveFramesBuffer = new ComputeBuffer(length, sizeof(float) * 12);
                var tVectorsBuffer = new ComputeBuffer(length, sizeof(float) * 4);
                
                tVectorsBuffer.SetData(tVectors);

                //3) Bind our compute buffers
                shaderBezierCompute.SetBuffer(shaderKernelComputeCurveFrames, TVECTORS_PID, tVectorsBuffer);
                shaderBezierCompute.SetBuffer(shaderKernelComputeCurveFrames, CURVE_FRAMES_PID, curveFramesBuffer);
                
                //4) Upload everything else that isn't a buffer
                shaderBezierCompute.SetMatrix(CONTROL_POINTS_PID, controlPointsMatrixCompute);
                shaderBezierCompute.SetMatrix(CONTROL_QUATERNIONS_PID, controlQuaternionsMatrix);
                shaderBezierCompute.SetFloat(FRAME_CORRECTION_PID, frameCorrection ? 1f : 0f);

                //5) Dispatch the shader
                var groupSize = length / 32 + 1;
                shaderBezierCompute.Dispatch(shaderKernelComputeCurveFrames, groupSize, 1, 1);

                //In runtime circumstances, this would cause a pretty big performance draw, but not critical for gizmo code
                curveFramesBuffer.GetData(curveFrames);
                
                //6) Dispose of our buffers when we're done with them
                curveFramesBuffer.Dispose();
                tVectorsBuffer.Dispose();
            }
            
            //Use each point to draw our segments
            for (var i = 0; i < curveFrames.Length - 1; i++)
            {
                Gizmos.color = Color.green;
                var frame0 = curveFrames[i];
                var frame1 = curveFrames[i + 1];

                Gizmos.DrawLine(frame0[0, 0, 0], frame1[0, 0, 0]);
                frame0.DrawGizmo();

            }
            curveFrames[curveSegmentCount].DrawGizmo();
            
            /*
            Gizmos.color = Color.red;
            var startFrame = curveFrames[0];
            
            Gizmos.DrawLine(startFrame[1, 1, 0],   startFrame[1, -1, 0]);
            Gizmos.DrawLine(startFrame[1, -1, 0],  startFrame[-1, -1, 0]);
            Gizmos.DrawLine(startFrame[-1, -1, 0], startFrame[-1, 1, 0]);
            Gizmos.DrawLine(startFrame[-1, 1, 0],  startFrame[1, 1, 0]);
            
            Gizmos.DrawLine(startFrame[1, 1, 0],   startFrame[0, 0, 1]);
            Gizmos.DrawLine(startFrame[1, -1, 0],  startFrame[0, 0, 1]);
            Gizmos.DrawLine(startFrame[-1, -1, 0], startFrame[0, 0, 1]);
            Gizmos.DrawLine(startFrame[-1, 1, 0],  startFrame[0, 0, 1]);
            
            Gizmos.DrawLine(startFrame[1, 1, 0],   curveFrames[1][1, 1, 0]);
            Gizmos.DrawLine(startFrame[1, -1, 0],  curveFrames[1][1, -1, 0]);
            Gizmos.DrawLine(startFrame[-1, -1, 0], curveFrames[1][-1, -1, 0]);
            Gizmos.DrawLine(startFrame[-1, 1, 0],  curveFrames[1][-1, 1, 0]);
            
            //Use each point to draw our segments
            for (var i = 1; i < curveFrames.Length - 1; i++)
            {
                Gizmos.color = Color.green;
                var frame0 = curveFrames[i];
                var frame1 = curveFrames[i + 1];
                
                Gizmos.DrawLine(frame0[1, 1, 0], frame0[1, -1, 0]);
                Gizmos.DrawLine(frame0[1, -1, 0], frame0[-1, -1, 0]);
                Gizmos.DrawLine(frame0[-1, -1, 0], frame0[-1, 1, 0]);
                Gizmos.DrawLine(frame0[-1, 1, 0], frame0[1, 1, 0]);
                
                Gizmos.DrawLine(frame0[1, 1, 0], frame1[1, 1, 0]);
                Gizmos.DrawLine(frame0[1, -1, 0], frame1[1, -1, 0]);
                Gizmos.DrawLine(frame0[-1, -1, 0], frame1[-1, -1, 0]);
                Gizmos.DrawLine(frame0[-1, 1, 0], frame1[-1, 1, 0]);

            }

            Gizmos.color = Color.blue;
            var endFrame = curveFrames[curveSegmentCount];
            
            Gizmos.DrawLine(endFrame[1, 1, 0],   endFrame[1, -1, 0]);
            Gizmos.DrawLine(endFrame[1, -1, 0],  endFrame[-1, -1, 0]);
            Gizmos.DrawLine(endFrame[-1, -1, 0], endFrame[-1, 1, 0]);
            Gizmos.DrawLine(endFrame[-1, 1, 0],  endFrame[1, 1, 0]);
            
            Gizmos.DrawLine(endFrame[1, 1, 0],   endFrame[0, 0, 5]);
            Gizmos.DrawLine(endFrame[1, -1, 0],  endFrame[0, 0, 5]);
            Gizmos.DrawLine(endFrame[-1, -1, 0], endFrame[0, 0, 5]);
            Gizmos.DrawLine(endFrame[-1, 1, 0],  endFrame[0, 0, 5]);
            */
            
        }
    }

    //This attribute tells us that we want to marshal this structure's
    //fields in the actual order they appear in the code
    [StructLayout(LayoutKind.Sequential)]
    private struct BezierFrame
    {
        public float3 Point;
        public float3 Right;
        public float3 Up;
        public float3 Tangent;
        
        public float3 this[float x, float y, float z] => Point + 
                                                         x * Right + 
                                                         y * Up + 
                                                         z * Tangent;
        public void DrawGizmo()
        {
            var currentColor = Gizmos.color;
            
            Gizmos.color = Color.green;
            Gizmos.DrawLine(this[0, 0, 0], this[0, 2, 0]);
            Gizmos.DrawLine(this[-0.5f, 1, 0], this[0.5f, 1, 0]);
            
            Gizmos.color = Color.red;
            Gizmos.DrawLine(this[0, 0, 0], this[2, 0, 0]);
            Gizmos.DrawLine(this[1, 0.5f, 0], this[1, -0.5f, 0]);
            
            Gizmos.color = Color.magenta;
            Gizmos.DrawLine(this[0, 0, 0], this[0, -2, 0]);
            Gizmos.DrawLine(this[-0.5f, -1, 0], this[0.5f, -1, 0]);
            
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(this[0, 0, 0], this[-2, 0, 0]);
            Gizmos.DrawLine(this[-1, 0.5f, 0], this[-1, -0.5f, 0]);

            Gizmos.color = currentColor;
        }
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex{
        public float3 PositionOS;
        public float3 NormalOS;
        public float2 UV;
        public Color Color;
        public int FrameOffset;
    }
    
    private struct Triangle
    {

        public Vertex Vertex_1;
        public Vertex Vertex_2;
        public Vertex Vertex_3;

        public Vertex this[int i]
        {
            get
            {
                switch (i)
                {
                    case 0:
                        return Vertex_1;
                    case 1:
                        return Vertex_2;
                    case 2:
                        return Vertex_3;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            set
            {
                switch (i)
                {
                    case 0:
                        Vertex_1 = value;
                        break;
                    case 1:
                        Vertex_2 = value;
                        break;
                    case 2:
                        Vertex_3 = value;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }
    }

    public enum MeshRotationMode
    {
        flexible, alwaysUP, preferUp,
    }
    
}
