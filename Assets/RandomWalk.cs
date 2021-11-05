using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Burst;
using Unity.Jobs;
using Unity.Collections;
using Unity.Mathematics;

public class RandomWalk : MonoBehaviour {

    [Header("Simulation")]
    public float StepsPerSecond = 30;
    public float MaxDeltaTime = 1 / 15.0f;

    [Header("Rendering")]
    public int VerticesPerMesh = 4096;
    public Material Material;
    public float FadeTime = 1;

    [Header("Camera")]
    public Camera Camera;
    public float CameraPositionLerp;
    public float CameraZoomDelta;

    [Header("Runtime")]
    public List<Mesh> Meshes = new List<Mesh>();
    public Mesh CurrentMesh = null;

    private RandomWalkJob _job;
    private float _stepsToTake = 0;

    private List<Vector3> _vertices = new List<Vector3>();
    private List<Vector4> _vertData = new List<Vector4>();
    private List<int> _indices = new List<int>();

    private void OnEnable() {
        _job = new RandomWalkJob() {
            Map = new Map(Allocator.Persistent),
            Directions = new NativeArray<int2>(new int2[] {
                new int2(1, 0),
                new int2(0, 1),
                new int2(-1, 0),
                new int2(0, -1)
            }, Allocator.Persistent),
            AddedElements = new NativeList<Element>(Allocator.Persistent),
            StateRef = new NativeReference<SimState>(Allocator.Persistent),
        };

        ResetState();
    }

    private void OnDisable() {
        _job.Map.Dispose();
        _job.Directions.Dispose();
        _job.AddedElements.Dispose();
        _job.StateRef.Dispose();
    }

    private void ResetState() {
        _job.Map.Clear();
        _job.AddedElements.Clear();
        _job.StateRef.Value = new SimState() {
            Position = new int2(0, 0),
            Distance = 0,
            Winding = 0,
            R = Unity.Mathematics.Random.CreateFromIndex((uint)DateTime.Now.GetHashCode())
        };

        foreach (var mesh in Meshes) {
            DestroyImmediate(mesh);
        }
        Meshes.Clear();
    }

    private void Update() {
        if (Input.GetKeyDown(KeyCode.Space)) {
            ResetState();
        }

        Simulate();

        Render();
    }

    private void Simulate() {
        _stepsToTake += StepsPerSecond * Mathf.Min(MaxDeltaTime, Time.deltaTime);
        int stepsInt = (int)_stepsToTake;
        _stepsToTake -= stepsInt;

        if (stepsInt > 0) {
            _job.StepsToTake = stepsInt;
            _job.Run();

            //Only update meshes if we actually stepped, since we use the most recently
            //added elements to update the current mesh
            UpdateMeshes();
        }

        Camera.transform.position = Vector3.Lerp(Camera.transform.position, new Vector3(_job.StateRef.Value.Position.x, _job.StateRef.Value.Position.y, 0), CameraPositionLerp);
        Camera.orthographicSize *= Mathf.Pow(CameraZoomDelta, -Input.mouseScrollDelta.y);
    }

    private void UpdateMeshes() {
        if (CurrentMesh == null) {
            CurrentMesh = new Mesh();
            CurrentMesh.name = "Render Mesh " + Meshes.Count;
            CurrentMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            Meshes.Add(CurrentMesh);

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

        CurrentMesh.SetVertices(_vertices);
        CurrentMesh.SetUVs(0, _vertData);
        CurrentMesh.SetIndices(_indices, MeshTopology.LineStrip, 0);

        if (_vertices.Count >= VerticesPerMesh) {
            CurrentMesh = null;
        }
    }

    private void Render() {
        Material.SetFloat("_MaxDistance", _job.StateRef.Value.Distance / (float)VerticesPerMesh);
        Material.SetFloat("_FadeDist", FadeTime * StepsPerSecond / VerticesPerMesh);

        foreach (var mesh in Meshes) {
            Graphics.DrawMesh(mesh, Matrix4x4.identity, Material, 0);
        }
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

        public NativeList<Element> AddedElements;

        public int StepsToTake;

        public NativeReference<SimState> StateRef;

        public void Execute() {
            var state = StateRef.Value;
            var possibilities = new NativeArray<int>(4, Allocator.Temp);

            AddedElements.Clear();
            for (; StepsToTake > 0; StepsToTake--) {
                int possibilitiesCount = 0;
                for (int i = 0; i < 3; i++) {
                    int newWinding = state.Winding + (i - 1);

                    int2 forwardDir = Directions[newWinding & 3];
                    int2 normalDir = new int2(-forwardDir.y, forwardDir.x);

                    //Easy reject if the position right in front is occupied
                    if (Map[state.Position + forwardDir].IsOccupied) {
                        continue;
                    }

                    //Now we need to validate the edges to our left and right to see if we are going
                    //to get ourselves trapped in a loop
                    bool isLeftSideValid = true;
                    bool isRightSideValid = true;

                    //We need to check all 3 edges for both sides, since any of them could end
                    //up trapping us if we take a step forward

                    for (int j = -1; j <= 1; j++) {
                        var leftCell = Map[state.Position + normalDir + forwardDir * j];
                        if (leftCell.IsOccupied && RelativeWinding(leftCell, newWinding) > 3) {
                            isLeftSideValid = false;
                            break;
                        }
                    }

                    if (!isLeftSideValid) {
                        continue;
                    }

                    for (int j = -1; j <= 1; j++) {
                        var rightCell = Map[state.Position - normalDir + forwardDir * j];
                        if (rightCell.IsOccupied && RelativeWinding(rightCell, newWinding) < -3) {
                            isRightSideValid = false;
                            break;
                        }
                    }

                    if (!isRightSideValid) {
                        continue;
                    }

                    possibilities[possibilitiesCount++] = newWinding;
                }

                //We have run into a dead end!  This should not happen in practice
                if (possibilitiesCount == 0) {
                    break;
                }

                //Choose a random winding from the set of valid windings
                int chosenWinding = possibilities[state.R.NextInt(possibilitiesCount)];

                Map[state.Position] = new Cell() {
                    IsOccupied = true,
                    Winding = chosenWinding,
                    Distance = state.Distance
                };

                AddedElements.Add(new Element() {
                    Position = state.Position,
                    Winding = chosenWinding,
                    Distance = state.Distance
                });

                state.Position += Directions[chosenWinding & 3];
                state.Winding = chosenWinding;
                state.Distance++;
            }

            //Remember to assign the state back to the reference when we are done
            possibilities.Dispose();
            StateRef.Value = state;
        }

        private int RelativeWinding(Cell cell, int winding) {
            return cell.Winding - winding;
        }
    }

    //Just a small wrapper around a hash map to act like an infinite grid
    public struct Map {

        private NativeHashMap<int2, Cell> _map;

        public Map(Allocator allocator) {
            _map = new NativeHashMap<int2, Cell>(4096, allocator);
        }

        public void Dispose() {
            _map.Dispose();
        }

        public void Clear() {
            _map.Clear();
        }

        public Cell this[int2 position] {
            get {
                if (_map.TryGetValue(position, out var cell)) {
                    return cell;
                } else {
                    return default;
                }
            }
            set {
                _map[position] = value;
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

    public struct SimState {
        public int2 Position;
        public int Winding;
        public int Distance;
        public Unity.Mathematics.Random R;
    }
}
