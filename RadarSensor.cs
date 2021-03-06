/**
 * Copyright (c) 2019-2021 LG Electronics, Inc.
 *
 * This software contains code licensed as described in LICENSE.
 *
 */

using System.Linq;
using System.Collections.Generic;
using Simulator.Bridge;
using Simulator.Bridge.Data;
using Simulator.Utilities;
using UnityEngine;
using UnityEngine.AI;
using Simulator.Sensors.UI;

namespace Simulator.Sensors
{
    [SensorType("Radar", new[] { typeof(DetectedRadarObjectData) })]
    public class RadarSensor : SensorBase
    {
        [SensorParameter]
        [Range(1.0f, 100f)]
        public float Frequency = 13.4f;
        public LayerMask RadarBlockers;

        private List<RadarMesh> radars = new List<RadarMesh>();
        private WireframeBoxes WireframeBoxes;

        private uint seqId;
        private float nextPublish;

        private BridgeInstance Bridge;
        private Publisher<DetectedRadarObjectData> Publish;
        private Rigidbody SelfRigidBody;
        
        private Dictionary<Collider, DetectedRadarObject> Detected = new Dictionary<Collider, DetectedRadarObject>();
        private Dictionary<Collider, Box> Visualized = new Dictionary<Collider, Box>();

        [AnalysisMeasurement(MeasurementType.Count)]
        public int MaxTracked = -1;
        
        public override SensorDistributionType DistributionType => SensorDistributionType.MainOrClient;
        public override float PerformanceLoad { get; } = 0.2f;
        
        struct Box
        {
            public Vector3 Size;
            public Color Color;
        }

        private void Awake()
        {
            radars.AddRange(GetComponentsInChildren<RadarMesh>());
            foreach (var radar in radars)
            {
                radar.Init();
            }
            SimulatorManager.Instance.NPCManager.RegisterDespawnCallback(OnExitRange);
        }

        protected override void Initialize()
        {
            Debug.Assert(SimulatorManager.Instance != null);
            WireframeBoxes = SimulatorManager.Instance.WireframeBoxes;
            foreach (var radar in radars)
            {
                radar.SetCallbacks(WhileInRange, OnExitRange);
            }
            nextPublish = Time.time + 1.0f / Frequency;

            SelfRigidBody = gameObject.GetComponentInParent<Rigidbody>();            
        }

        protected override void Deinitialize()
        {
            
        }

        private void Update()
        {
            if (Bridge == null || Bridge.Status != Status.Connected)
            {
                return;
            }

            if (Time.time < nextPublish)
            {
                return;
            }
                        
            nextPublish = Time.time + 1.0f / Frequency;
            MaxTracked = Mathf.Max(MaxTracked, Detected.Count);
            Publish(new DetectedRadarObjectData()
            {
                Name = Name,
                Frame = Frame,
                Time = SimulatorManager.Instance.CurrentTime,
                Sequence = seqId++,
                Data = Detected.Values.ToArray(),
            });
        }

        public override void OnBridgeSetup(BridgeInstance bridge)
        {
            Bridge = bridge;
            Publish = Bridge.AddPublisher<DetectedRadarObjectData>(Topic);
        }
        
        void WhileInRange(Collider other, RadarMesh radar)
        {
            if (other.isTrigger)
                return;

            if (!other.enabled)
                return;
            
            if (CheckBlocked(other))
            {
                if (Detected.ContainsKey(other))
                {
                    Detected.Remove(other);
                }
                if (Visualized.ContainsKey(other))
                {
                    Visualized.Remove(other);
                }
                return;
            }

            if (Detected.ContainsKey(other)) // update existing data
            {
                Vector3 objectVelocity = GetObjectVelocity(other);

                Detected[other].SensorPosition = transform.position;
                Detected[other].SensorAim = transform.forward;
                Detected[other].SensorRight = transform.right;
                Detected[other].SensorVelocity = GetSensorVelocity();
                Detected[other].SensorAngle = GetSensorAngle(other);
                Detected[other].Position = other.bounds.center;
                Detected[other].Velocity = objectVelocity;
                Detected[other].RelativePosition = other.bounds.center - transform.position;
                Detected[other].RelativeVelocity = GetSensorVelocity() - objectVelocity;
                Detected[other].ColliderSize = other.bounds.size;
                Detected[other].State = GetAgentState(other);
                Detected[other].NewDetection = false;
            }
            else
            {
                Box box = GetVisualizationBox(other);
                if (box.Size != Vector3.zero) // Empty box returned if tag is not right
                {
                    Vector3 objectVelocity = GetObjectVelocity(other);

                    Visualized.Add(other, box);
                    Detected.Add(other, new DetectedRadarObject()
                    {
                        Id = other.gameObject.GetInstanceID(),
                        SensorPosition = transform.position,
                        SensorAim = transform.forward,
                        SensorRight = transform.right,
                        SensorVelocity = GetSensorVelocity(),
                        SensorAngle = GetSensorAngle(other),
                        Position = other.bounds.center,
                        Velocity = objectVelocity,
                        RelativePosition = other.bounds.center - transform.position,
                        RelativeVelocity = GetSensorVelocity() - objectVelocity,
                        ColliderSize = other.bounds.size,
                        State = GetAgentState(other),
                        NewDetection = true,
                });
                }
            }
        }

        void OnExitRange(NPCController controller)
        {
            OnExitRange(controller.MainCollider);
        }

        void OnExitRange(Collider other, RadarMesh radar)
        {
            OnExitRange(other);
        }

        void OnExitRange(Collider other)
        {
            if (Detected.ContainsKey(other))
            {
                Detected.Remove(other);
            }

            if (Visualized.ContainsKey(other))
            {
                Visualized.Remove(other);
            }
        }

        private bool CheckBlocked(Collider col)
        {
            bool isBlocked = false;
            var orig = transform.position;
            var end = col.bounds.center;
            var dir = end - orig;
            var dist = (end - orig).magnitude;
            if (Physics.Raycast(orig, dir, out RaycastHit hit, dist, RadarBlockers)) // ignore if blocked
            {
                if (hit.collider != col)
                {
                    isBlocked = true;
                }
            }
            return isBlocked;
        }

        private Box GetVisualizationBox(Collider other)
        {
            var bbox = new Box();
            Vector3 size = Vector3.zero;

            if (other.gameObject.layer == LayerMask.NameToLayer("NPC"))
            {
                bbox.Color = Color.green;
            }
            else if (other.gameObject.layer == LayerMask.NameToLayer("Pedestrian"))
            {
                bbox.Color = Color.yellow;
            }
            else if (other.gameObject.layer == LayerMask.NameToLayer("Bicycle"))
            {
                bbox.Color = Color.cyan;
            }
            else if (other.gameObject.layer == LayerMask.NameToLayer("Agent"))
            {
                bbox.Color = Color.magenta;
            }
            else
            {
                return bbox;
            }

            if (other is BoxCollider)
            {
                var box = other as BoxCollider;
                bbox.Size = box.size;
                size.x = box.size.z;
                size.y = box.size.x;
                size.z = box.size.y;
            }
            else if (other is CapsuleCollider)
            {
                var capsule = other as CapsuleCollider;
                bbox.Size = new Vector3(capsule.radius * 2, capsule.height, capsule.radius * 2);
                size.x = capsule.radius * 2;
                size.y = capsule.radius * 2;
                size.z = capsule.height;
            }
            else if (other is MeshCollider)
            {
                var mesh = other as MeshCollider;
                var npcC = mesh.GetComponentInParent<NPCController>();
                var va = mesh.GetComponentInParent<IAgentController>();
                
                if (npcC != null)
                {
                    bbox.Size = npcC.Bounds.size;
                    size.x = npcC.Bounds.size.x;
                    size.y = npcC.Bounds.size.y;
                    size.z = npcC.Bounds.size.z;
                }

                if (va != null)
                {
                    bbox.Size = va.Bounds.size;
                    size.x = va.Bounds.size.z;
                    size.y = va.Bounds.size.x;
                    size.z = va.Bounds.size.y;
                }
            }

            return bbox;
        }

        private Vector3 GetSensorVelocity()
        {
            return SelfRigidBody == null ? Vector3.zero : SelfRigidBody.velocity;
        }

        private double GetSensorAngle(Collider col)
        {
            // angle is orientation of the obstacle in degrees as seen by radar, counterclockwise is positive
            double angle = -Vector3.SignedAngle(transform.forward, col.transform.forward, transform.up);
            if (angle > 90)
            {
                angle -= 180;
            }
            else if (angle < -90)
            {
                angle += 180;
            }
            return angle;
        }

        private Vector3 GetObjectVelocity(Collider col)
        {
            Vector3 velocity = Vector3.zero;
            var npc = col.GetComponentInParent<NPCController>();
            if (npc != null)
            {
                velocity = npc.simpleVelocity;
            }
            else
            {
                var ped = col.GetComponentInParent<NavMeshAgent>();
                if (ped != null)
                {
                    velocity = ped.desiredVelocity;
                }
                else
                {
                    var rb = col.attachedRigidbody;
                    if (rb != null)
                    {
                        velocity = rb.velocity;
                    }
                }
            }

            return velocity;
        }

        private int GetAgentState(Collider col)
        {
            int state = 1;
            if (col.GetComponent<NPCController>()?.currentSpeed > 1f)
            {
                state = 0;
            }
            return state;
        }

        public override void OnVisualize(Visualizer visualizer)
        {
            foreach (var v in Visualized)
            {
                var collider = v.Key;
                var box = v.Value;
                if (collider.gameObject.activeInHierarchy)
                {
                    WireframeBoxes.Draw(collider.gameObject.transform.localToWorldMatrix, new Vector3(0f, collider.bounds.extents.y, 0f), box.Size, box.Color);
                }
            }

            foreach (var radar in radars)
            {
                Graphics.DrawMesh(radar.GetComponent<MeshFilter>().sharedMesh, transform.localToWorldMatrix, radar.RadarMeshRenderer.sharedMaterial, LayerMask.NameToLayer("Sensor"));
            }
        }

        public override void OnVisualizeToggle(bool state) {}
    }
}
