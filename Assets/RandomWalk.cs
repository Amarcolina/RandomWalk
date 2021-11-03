using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Jobs;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using static Unity.Mathematics.math;

public class RandomWalk : MonoBehaviour {

    public float StepsPerSecond;
    public int Seed;
    public bool DoShowWinding;
    public float DistanceScale;
    public float RelevanceScale;

    [Header("Camera")]
    public Camera Camera;
    public float CameraPositionLerp;
    public float CameraZoomDelta;

    [Header("Meshes")]
    public int VerticesPerMesh = 4096;
    public Material Material;
    public float FadeTime = 1;

    private RandomWalkJob _job;

    
    [Header("Runtime")]
    [SerializeField]
    private List<Mesh> _meshes = new List<Mesh>();
    private Mesh _currentMesh = null;

    private float _stepsToTake = 0;

    private List<Vector3> _vertices = new List<Vector3>();
    private List<Vector4> _vertData = new List<Vector4>();
    private List<int> _indices = new List<int>();

    private void OnEnable() {
        _job = new RandomWalkJob() {
            Map = new Map() { HashMap = new NativeHashMap<int2, Cell>(128, Allocator.Persistent) },
            Directions = new NativeArray<int2>(new int2[] {
                new int2(1, 0),
                new int2(0, 1),
                new int2(-1, 0),
                new int2(0, -1)
            }, Allocator.Persistent),
            ValidWindings = new NativeArray<int>(4, Allocator.Persistent),
            Position = new NativeArray<int2>(1, Allocator.Persistent),
            Winding = new NativeArray<int>(1, Allocator.Persistent),
            Distance = new NativeArray<int>(1, Allocator.Persistent),
            AddedElements = new NativeList<Element>(Allocator.Persistent),
        };
    }

    private void OnDisable() {
        _job.Map.HashMap.Dispose();
        _job.Directions.Dispose();
        _job.ValidWindings.Dispose();
        _job.Position.Dispose();
        _job.Winding.Dispose();
        _job.Distance.Dispose();
        _job.AddedElements.Dispose();
    }

    private void Update() {
        if (Input.GetKeyDown(KeyCode.Space)) {
            _job.Map.HashMap.Clear();
            _job.Position[0] = new int2(0, 0);
            _job.Winding[0] = 0;
            _job.Distance[0] = 0;

            foreach (var mesh in _meshes) {
                DestroyImmediate(mesh);
            }
            _meshes.Clear();

            _job.AddedElements.Clear();
        }

        _stepsToTake += StepsPerSecond * Time.deltaTime;
        int stepsInt = (int)_stepsToTake;
        _stepsToTake -= stepsInt;

        if (stepsInt != 0) {
            _job.StepsToTake = stepsInt;
            _job.R = Unity.Mathematics.Random.CreateFromIndex((uint)(Seed + Time.frameCount));
            _job.Run();

            if (_currentMesh == null) {
                _currentMesh = new Mesh();
                _currentMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                _meshes.Add(_currentMesh);
                _vertices.Clear();
                _vertData.Clear();
                _indices.Clear();

                _vertices.Add(new Vector3(_job.AddedElements[0].Position.x, _job.AddedElements[0].Position.y));
                _vertData.Add(new Vector4(_job.AddedElements[0].Distance / (float)VerticesPerMesh, _job.AddedElements[0].Winding, 0, 0));
                _indices.Add(0);
            }

            foreach (var element in _job.AddedElements) {
                var end = element.Position + _job.Directions[element.Winding & 3];
                var pos = new Vector3(end.x, end.y, 0);

                _vertices.Add(pos);
                _vertData.Add(new Vector4(element.Distance / (float)VerticesPerMesh, element.Winding, 0, 0));
                _indices.Add(_vertices.Count - 1);
            }

            _currentMesh.SetVertices(_vertices);
            _currentMesh.SetUVs(0, _vertData);
            _currentMesh.SetIndices(_indices, MeshTopology.LineStrip, 0);

            if (_vertices.Count >= VerticesPerMesh) {
                _currentMesh = null;
            }
        }

        Material.SetFloat("_MaxDistance", _job.Distance[0] / (float)VerticesPerMesh);
        Material.SetFloat("_FadeDist", FadeTime * StepsPerSecond / (float)VerticesPerMesh);

        foreach (var mesh in _meshes) {
            Graphics.DrawMesh(mesh, Matrix4x4.identity, Material, 0);
        }

        Camera.transform.position = Vector3.Lerp(Camera.transform.position, new Vector3(_job.Position[0].x, _job.Position[0].y, 0), CameraPositionLerp);
        Camera.orthographicSize *= Mathf.Pow(CameraZoomDelta, -Input.mouseScrollDelta.y);
    }

    private void OnDrawGizmos() {
        //if (!Application.isPlaying) {
        //    return;
        //}

        //foreach (var pair in _job.Map.HashMap) {
        //    var pos = new Vector3(pair.Key.x, pair.Key.y, 0);
        //    var winding = pair.Value.Winding;
        //    var relevance = _job.Distance[0] - pair.Value.Distance;
        //    var dir = _job.Directions[winding & 3];

        //    float wow = Mathf.Clamp01((RelevanceScale - relevance) / RelevanceScale);

        //    //Gizmos.color = Color.HSVToRGB((winding & 31) / 32.0f % 1.0f, 1.0f, 1.0f);
        //    Gizmos.color = Color.HSVToRGB(pair.Value.Distance / DistanceScale % 1.0f, wow, wow);
        //    Gizmos.DrawLine(pos, pos + new Vector3(dir.x, dir.y, 0));
        //}
    }

    private void OnGUI() {
        float sliderPow = 4;

        using (new GUILayout.HorizontalScope()) {
            GUILayout.Label("Steps-per-second:");

            float sliderVal = Mathf.Pow(StepsPerSecond, 1.0f / sliderPow);
            sliderVal = GUILayout.HorizontalSlider(sliderVal, 1, 12, GUILayout.Width(200));
            StepsPerSecond = Mathf.Pow(sliderVal, sliderPow);

            GUILayout.Label(StepsPerSecond.ToString());
        }
    }

    [BurstCompile]
    public struct RandomWalkJob : IJob {

        public Map Map;
        public NativeArray<int2> Directions;
        public NativeArray<int> ValidWindings;

        public NativeList<Element> AddedElements;

        public NativeArray<int2> Position;
        public NativeArray<int> Winding;
        public NativeArray<int> Distance;

        public int StepsToTake;

        public Unity.Mathematics.Random R;

        public void Execute() {
            AddedElements.Clear();
            for (; StepsToTake > 0; StepsToTake--) {

                int2 pos = Position[0];

                int validWindings = 0;
                for (int i = 0; i < 3; i++) {
                    int newWinding = Winding[0] + (i - 1);

                    int2 forwardDir = Directions[newWinding & 3];
                    int2 normalDir = new int2(-forwardDir.y, forwardDir.x);

                    //Easy reject
                    if (Map[pos + forwardDir].IsOccupied) {
                        continue;
                    }

                    var leftA = Map[pos + normalDir];
                    var leftB = Map[pos + normalDir + forwardDir];
                    var leftC = Map[pos + normalDir - forwardDir];

                    if (leftA.IsOccupied && RelativeWinding(leftA, newWinding) > 3) {
                        continue;
                    }

                    if (leftB.IsOccupied && RelativeWinding(leftB, newWinding) > 3) {
                        continue;
                    }

                    if (leftC.IsOccupied && RelativeWinding(leftC, newWinding) > 3) {
                        continue;
                    }

                    var rightA = Map[pos - normalDir];
                    var rightB = Map[pos - normalDir + forwardDir];
                    var rightC = Map[pos - normalDir - forwardDir];

                    if (rightA.IsOccupied && RelativeWinding(rightA, newWinding) < -3) {
                        continue;
                    }

                    if (rightB.IsOccupied && RelativeWinding(rightB, newWinding) < -3) {
                        continue;
                    }

                    if (rightC.IsOccupied && RelativeWinding(rightC, newWinding) < -3) {
                        continue;
                    }

                    ValidWindings[validWindings++] = newWinding;
                }

                if (validWindings == 0) {
                    break;
                }

                int chosenWinding = ValidWindings[R.NextInt(validWindings)];

                Map[Position[0]] = new Cell() {
                    IsOccupied = true,
                    Winding = chosenWinding,
                    Distance = Distance[0]
                };

                AddedElements.Add(new Element() {
                    Position = Position[0],
                    Winding = chosenWinding,
                    Distance = Distance[0]
                });

                Position[0] += Directions[chosenWinding & 3];
                Winding[0] = chosenWinding;
                Distance[0]++;
            }
        }

        private int RelativeWinding(Cell cell, int winding) {
            return cell.Winding - winding;
        }
    }

    public struct Map {

        public NativeHashMap<int2, Cell> HashMap;

        public Cell this[int2 position] {
            get {
                if (HashMap.TryGetValue(position, out var cell)) {
                    return cell;
                } else {
                    return default;
                }
            }
            set {
                HashMap[position] = value;
            }
        }
    }

    public struct Cell {
        public bool IsOccupied;
        public int Winding;
        public int Distance;
    }

    public struct Element {
        public int2 Position;
        public int Winding;
        public int Distance;
    }


}
