﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Xbim.Common.Geometry;
using Xbim.Tessellator;

namespace Xbim.ModelGeometry.Scene
{


    public class XbimTriangulatedMesh
    {
        public struct  XbimTriangle
        {
            readonly XbimContourVertexCollection _vertices;
            readonly XbimTriangleEdge[] _edges;

            internal XbimTriangle(XbimTriangleEdge[] edges, XbimContourVertexCollection vertices)
            {
                _vertices = vertices;
                _edges = edges;
            }

            public bool IsEmpty
            {
                get { return _edges == null; }
            }

            public XbimVector3D Normal
            {
                get
                {
                    var p1 = _vertices[_edges[0].StartVertexIndex].Position;
                    var p2 = _vertices[_edges[0].NextEdge.StartVertexIndex].Position;
                    var p3 = _vertices[_edges[0].NextEdge.NextEdge.StartVertexIndex].Position;
                    var a = new XbimPoint3D(p1.X, p1.Y, p1.Z);
                    var b = new XbimPoint3D(p2.X, p2.Y, p2.Z);
                    var c = new XbimPoint3D(p3.X, p3.Y, p3.Z);
                    var cv = XbimVector3D.CrossProduct(b - a, c - a);
                    cv.Normalize();
                    return cv;
                } 
            }
            public XbimPackedNormal PackedNormal
            {
                get
                {
                    return new XbimPackedNormal(Normal);
                }
            }
        }

        private readonly Dictionary<long, XbimTriangleEdge[]> _lookupList;
        private readonly List<XbimTriangleEdge[]> _faultyTriangles = new List<XbimTriangleEdge[]>();
        private Dictionary<int, List<XbimTriangleEdge[]>> _faces;
        private readonly XbimContourVertexCollection _vertices;
        
        double _minX = double.PositiveInfinity;
        double _minY = double.PositiveInfinity;
        double _minZ = double.PositiveInfinity;
        double _maxX = double.NegativeInfinity;
        double _maxY = double.NegativeInfinity;
        double _maxZ = double.NegativeInfinity;
        private XbimTriangleEdge _extremeEdge;
        public XbimTriangulatedMesh(int faceCount, float precision)
        {
            var edgeCount = (int)(faceCount * 1.5);
            _lookupList = new Dictionary<long, XbimTriangleEdge[]>(edgeCount);
            _faces = new Dictionary<int, List<XbimTriangleEdge[]>>(faceCount);
            _vertices = new XbimContourVertexCollection(precision);
           
        }

        public uint TriangleCount
        {
            get
            {
                uint triangleCount = 0;
                foreach (var face in _faces.Values)
                    triangleCount += (uint)face.Count;
                return triangleCount;
            }
        }
        public IEnumerable<XbimTriangle> Triangles
        {
            get 
            {
                return from edgeListList in _faces.Values 
                       from edges in edgeListList 
                       select new XbimTriangle(edges, _vertices);
            }
        }
        public List<XbimTriangleEdge[]> FaultyTriangles
        {
            get { return _faultyTriangles; }
        }

        /// <summary>
        /// Returns the normal of the triangle that contains the specified edge
        /// </summary>
        /// <param name="edge"></param>
        /// <returns></returns>
        public XbimVector3D TriangleNormal(XbimTriangleEdge edge)
        {
            var p1 = _vertices[edge.StartVertexIndex].Position;
            var p2 = _vertices[edge.NextEdge.StartVertexIndex].Position;
            var p3 = _vertices[edge.NextEdge.NextEdge.StartVertexIndex].Position;    
            var a = new XbimPoint3D(p1.X,p1.Y,p1.Z); 
            var b = new XbimPoint3D(p2.X,p2.Y,p2.Z); 
            var c = new XbimPoint3D(p3.X,p3.Y,p3.Z);
            var cv = XbimVector3D.CrossProduct(b - a, c - a );
            cv.Normalize();
            return cv;
        }

        public Dictionary<int, List<XbimTriangleEdge[]>> Faces
        {
            get
            {
                return _faces;
            }
        }

       
        private bool AddEdge(XbimTriangleEdge edge)
        {
            if (_extremeEdge == null) _extremeEdge = edge;
            var key = edge.Key;
            if (!_lookupList.ContainsKey(key))
            {
                var arr = new XbimTriangleEdge[2];
                arr[0] = edge;
                _lookupList[key] = arr;
            }
            else
            {
                var edges = _lookupList[key];
                if (edges[1] != null)
                    return false; //we already have a pair
                edges[1] = edge;
                edges[0].AdjacentEdge = edge;
                edge.AdjacentEdge = edges[0];
            }
            if (_vertices[edge.StartVertexIndex].Position.Z > _vertices[_extremeEdge.StartVertexIndex].Position.Z && Vec3.Colinear(_vertices[edge.StartVertexIndex].Position, _vertices[edge.NextEdge.StartVertexIndex].Position, _vertices[edge.NextEdge.StartVertexIndex].Position))
                _extremeEdge = edge;
            return true;
        }

        /// <summary>
        /// Orientates edges to orientate in a uniform direction
        /// </summary>
        /// <returns></returns>
        public void UnifyFaceOrientation(int entityLabel)
        {
            if (_extremeEdge==null || _extremeEdge.IsEmpty) 
                return;
            if (!IsFacingOutward(_extremeEdge)) _extremeEdge.Reverse();
            var triangle = new List<XbimTriangleEdge[]>
            {
                new[] {_extremeEdge, _extremeEdge.NextEdge, _extremeEdge.NextEdge.NextEdge}
            };
            _extremeEdge.Freeze();
            do
            {
                 triangle = UnifyConnectedTriangles(triangle);
            } while (triangle.Any());
           
            //doing the extreme edge first should do all connected

            foreach (var xbimEdges in _faces.Values.SelectMany(el => el).Where(e => !e[0].Frozen)) //check any rogue elements
            {
                if (!IsFacingOutward(xbimEdges[0])) xbimEdges[0].Reverse();
                triangle = new List<XbimTriangleEdge[]> { new[] { xbimEdges[0], xbimEdges[0].NextEdge, xbimEdges[0].NextEdge.NextEdge } };
                xbimEdges[0].Freeze();
                do
                {
                    triangle = UnifyConnectedTriangles(triangle);
                } while (triangle.Any());

            }
            BalanceNormals();
        }

        public void BalanceNormals()
        {
            const double minAngle = Math.PI / 6;

            //set up the base normals
            foreach (var faceGroup in Faces)
            {
                foreach (var triangle in faceGroup.Value)
                {
                    ComputeTriangleNormal(triangle);
                }
            }

            var edgesAtVertex = _faces.Values.SelectMany(el => el).SelectMany(e => e).Where(e => e != null).GroupBy(k => k.StartVertexIndex);
            foreach (var edges in edgesAtVertex)
            {
                //create a set of faces to divide the point into a set of connected faces               
                var faceSet = new List<List<XbimTriangleEdge>>();//the first face set at this point

                //find an unconnected edge if one exists
                var unconnectedEdges = edges.Where(e => e.AdjacentEdge == null);
                var freeEdges = unconnectedEdges as IList<XbimTriangleEdge> ?? unconnectedEdges.ToList();

                if (!freeEdges.Any())
                //they are all connected to each other so find the first sharp edge or the any one if none sharp, this stops a face being split
                {
                    XbimTriangleEdge nextConnectedEdge = edges.First();
                    freeEdges = new List<XbimTriangleEdge>(1) { edges.First() }; //take the first if we don't find a sharp edge
                    //now look for any connected edges 
                    var visited = new HashSet<long>();
                    do
                    {
                        visited.Add(nextConnectedEdge.EdgeId);
                        nextConnectedEdge = nextConnectedEdge.NextEdge.NextEdge.AdjacentEdge;
                        if (nextConnectedEdge != null && visited.Contains(nextConnectedEdge.EdgeId)) break; //we are looping or at the start
                        if (nextConnectedEdge != null)
                        {
                            //if the edge is sharp start here
                            if (nextConnectedEdge.Angle > minAngle)
                            {
                                freeEdges = new List<XbimTriangleEdge>(1) { nextConnectedEdge };
                                break;
                            }

                        }
                    } while (nextConnectedEdge != null);
                }

                foreach (var edge in freeEdges)
                {
                    var face = new List<XbimTriangleEdge> { edge };
                    faceSet.Add(face);
                    XbimTriangleEdge nextConnectedEdge = edge;
                    //now look for any connected edges 
                    var visited = new HashSet<long>();
                    do
                    {
                        visited.Add(nextConnectedEdge.EdgeId);
                        nextConnectedEdge = nextConnectedEdge.NextEdge.NextEdge.AdjacentEdge;
                        if (nextConnectedEdge != null && visited.Contains(nextConnectedEdge.EdgeId)) break; //we are looping or at the start
                        if (nextConnectedEdge != null)
                        {
                            //if the edge is sharp start a new face
                            if (nextConnectedEdge.Angle > minAngle)
                            {
                                face = new List<XbimTriangleEdge>();
                                faceSet.Add(face);
                            }
                            face.Add(nextConnectedEdge);
                        }
                    } while (nextConnectedEdge != null);
                    //move on to next face

                }

                //we have our smoothing groups
                foreach (var vertexEdges in faceSet.Where(f => f.Count > 1))
                {
                    var vertexNormal = Vec3.Zero;
                    foreach (var edge in vertexEdges)
                        if (edge.Normal.IsValid) Vec3.AddTo(ref vertexNormal, ref edge.Normal);
                    Vec3.Normalize(ref vertexNormal);
                    foreach (var edge in vertexEdges)
                        edge.Normal = vertexNormal;
                }


            }//now convert faces
            _faces = _faces.Values.SelectMany(v => v).GroupBy(t=>(int)ComputeTrianglePackedNormal(t).ToUnit16()).ToDictionary(k=>k.Key,v=>v.ToList());
        }




        private List<XbimTriangleEdge[]> UnifyConnectedTriangles(List<XbimTriangleEdge[]> triangles)
        {
            var nextCandidates = new List<XbimTriangleEdge[]>();
            foreach (var triangle in triangles)
            {
                foreach (var edge in triangle)
                {
                    var adjacentEdge = edge.AdjacentEdge;
                    
                    if (adjacentEdge != null) //if we just have one it is a boundary
                    {
                        var adjacentTriangle = new[] {adjacentEdge, adjacentEdge.NextEdge, adjacentEdge.NextEdge.NextEdge};
                        if (adjacentEdge.EdgeId == edge.EdgeId) //they both face the same way
                        {
                            if (!adjacentEdge.Frozen)
                            {
                                adjacentEdge.Reverse(); //will reverse the entire triangle
                            }
                            else //we cannot align the edges correctly so break the connection
                            {
                                edge.AdjacentEdge = null;
                                adjacentEdge.AdjacentEdge = null; 
                                //Xbim3DModelContext.Logger.WarnFormat("Invalid triangle orientation has been ignored in entity #{0}", entityLabel);
                            }
                          
                        }
                        
                        if (!adjacentEdge.Frozen)
                        {
                            adjacentEdge.Freeze();
                            nextCandidates.Add(adjacentTriangle);
                        } 
                    }
                   
                }
            }
            return nextCandidates;
        }

        /// <summary>
        /// Adds the triangle using the three ints as inidices into the vertext collection
        /// </summary>
        /// <param name="p1"></param>
        /// <param name="p2"></param>
        /// <param name="p3"></param>
        /// <param name="faceId"></param>
        public void AddTriangle(int p1, int p2, int p3, int faceId)
        {
            var e1 = new XbimTriangleEdge(p1);
            var e2 = new XbimTriangleEdge(p2);
            var e3 = new XbimTriangleEdge(p3);
            e1.NextEdge = e2;
            e2.NextEdge = e3;
            e3.NextEdge = e1;
            //if(!ComputeTriangleNormal(new []{e1,e2,e3}) ) 
            //    return; //don't add lines
           
            var edgeList = new[] { e1, e2, e3 };
            bool faulty = !AddEdge(e1);
            if (!faulty && !AddEdge(e2))
            {
                RemoveEdge(e1);
                faulty = true;
            }
            if (!faulty && !AddEdge(e3))
            {
                RemoveEdge(e1);
                RemoveEdge(e2);
                faulty = true;
            }
            if (faulty) 
                FaultyTriangles.Add(edgeList);
            List<XbimTriangleEdge[]> triangleList;
            if (!_faces.TryGetValue(faceId, out triangleList))
            {
                triangleList = new List<XbimTriangleEdge[]>();
                _faces.Add(faceId, triangleList);
            }
            triangleList.Add(edgeList);
            
        }

        /// <summary>
        /// Computes the packed normal for the triangle, if all the normals at each vertex are the same it is returned, if any are different XbimPackedNormal.Invalid is returned. Assumes the normals have been calculated and balanced
        /// </summary>
        /// <param name="edges"></param>
        private XbimPackedNormal ComputeTrianglePackedNormal(XbimTriangleEdge[] edges)
        {
            var pn = edges[0].PackedNormal;
            var pn0 = pn.ToUnit16();
            var pn1 = edges[1].PackedNormal.ToUnit16();
            var pn2 = edges[2].PackedNormal.ToUnit16();
            if (pn0 == pn1 && pn1 == pn2) return pn;
            return new XbimPackedNormal(255,255);
        }

        /// <summary>
        /// Calculates the normal for a connected triangle edge, assumes the edge is part of a complete triangle and there are 3 triangle edges
        /// </summary>
        /// <param name="edge"></param>
        public bool ComputeTriangleNormal(XbimTriangleEdge[] edges)
        {
            var p1 = _vertices[edges[0].StartVertexIndex].Position;
            var p2 = _vertices[edges[0].NextEdge.StartVertexIndex].Position;
            var p3 = _vertices[edges[0].NextEdge.NextEdge.StartVertexIndex].Position;

            var ax = p1.X; var bx = p2.X; var cx = p3.X;
            var ay = p1.Y; var by = p2.Y; var cy = p3.Y;
            var az = p1.Z; var bz = p2.Z; var cz = p3.Z;

            // calculate normal of a triangle
            edges[0].Normal.X = (by - ay) * (cz - az) - (bz - az) * (cy - ay);
            edges[0].Normal.Y = (bz - az) * (cx - ax) - (bx - ax) * (cz - az);
            edges[0].Normal.Z = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);

            if (Vec3.Normalize(ref edges[0].Normal))
            {
                edges[1].Normal = edges[0].Normal;
                edges[2].Normal = edges[0].Normal;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Removes an edge from the edge list
        /// </summary>
        /// <param name="edge"></param>
        private void RemoveEdge(XbimTriangleEdge edge)
        {
            var edges = _lookupList[edge.Key];
            if (edges[0] == edge) //if it is the first one 
            {
                if (edges[1] == null) //and there is no second one
                    _lookupList.Remove(edge.Key); //remove the entire key
                else
                    edges[0] = edges[1]; //keep the second one
            }
            if (edges[1] == edge) //if it is the second one just remove it and leave the first
                edges[1] = null;
        }

        public void AddVertex(Vec3 v, ref ContourVertex contourVertex)
        {
            if (_vertices.Contains(v)) 
                contourVertex = _vertices[v];
            else
            {
                _vertices.Add(v, ref contourVertex);
                _minX = Math.Min(_minX, v.X);
                _minY = Math.Min(_minY, v.Y);
                _minZ = Math.Min(_minZ, v.Z);
                _maxX = Math.Max(_maxX, v.X);
                _maxY = Math.Max(_maxY, v.Y);
                _maxZ = Math.Max(_maxZ, v.Z);
            }
        }

        public uint VertexCount
        {
            get { return (uint)_vertices.Count; }
        }

        public IEnumerable<Vec3> Vertices
        {
            get { return _vertices.Select(c => c.Position); }
        }


        public XbimRect3D BoundingBox
        {
            get { return new XbimRect3D(_minX, _minY, _minZ, _maxX - _minX, _maxY - _minY, _maxZ - _minZ); }
        }

        public XbimPoint3D Centroid
        {
            get { return BoundingBox.Centroid(); }
        }

        public XbimVector3D PointingOutwardFrom(XbimPoint3D point3D)
        {
            var v = point3D - Centroid;
            v.Normalize();
            return v;
        }

        /// <summary>
        /// Returns true if the triangle that contains the edge is facing away from the centroid of the mesh
        /// </summary>
        /// <param name="triangleEdge"></param>
        /// <returns></returns>
        public bool IsFacingOutward(XbimTriangleEdge triangleEdge)
        {
            var normal = TriangleNormal(triangleEdge);
            var aVec = _vertices[triangleEdge.StartVertexIndex].Position;
            var aPoint = new XbimPoint3D(aVec.X,aVec.Y,aVec.Z);
            var vecOut = PointingOutwardFrom(aPoint);
            var dot = vecOut.DotProduct(normal);
            return dot > 0;
        }

    }

}

/// <summary>
/// Edge class for triangular meshes only
/// </summary>
public class XbimTriangleEdge
{
    public int StartVertexIndex;
    public XbimTriangleEdge NextEdge;
    public XbimTriangleEdge AdjacentEdge;
    public Vec3 Normal;
    private bool _frozen;
    public int EndVertexIndex { get { return NextEdge.StartVertexIndex; } }
    public XbimTriangleEdge(int p1)
    {
        StartVertexIndex = p1;
    }

    public bool Frozen
    {
        get { return _frozen; }
        
    }

    /// <summary>
    /// Returns the angle of this edge, 0 if the edge has no adjacent edge or the the normals are invalid
    /// </summary>
    public double Angle
    {
        get
        {
            if (AdjacentEdge != null && Normal.IsValid && AdjacentEdge.NextEdge.Normal.IsValid)
                return Vec3.Angle(ref Normal, ref AdjacentEdge.NextEdge.Normal);
            return 0;
        }    
        
    }
    public void Freeze()
    {
        _frozen = true;
        NextEdge._frozen=true;
        NextEdge.NextEdge._frozen = true;
    }


    public void Reverse()
    {
        if (!_frozen)
        { 
            var p1 = StartVertexIndex;
            var p2 = NextEdge.StartVertexIndex;
            var p3 = NextEdge.NextEdge.StartVertexIndex;
            StartVertexIndex = p2;
            NextEdge.StartVertexIndex = p3;
            NextEdge.NextEdge.StartVertexIndex = p1;
            var prevEdge = NextEdge.NextEdge;
            prevEdge.NextEdge = NextEdge;
            NextEdge.NextEdge = this;
            NextEdge = prevEdge;
        }
       
    }

    /// <summary>
    /// The ID of the edge, unique for all edges between vertices
    /// </summary>
    public long EdgeId
    {
        get
        {
            long a = StartVertexIndex;
            a <<= 32;
            return (a | (uint)EndVertexIndex);
        }
    }

    public bool IsEmpty { get { return EdgeId == 0; } }

    /// <summary>
    /// The key for the edge, this is the same for both directions of an  edge
    /// </summary>
    public long Key
    {
        get
        {
            long left = Math.Max(StartVertexIndex, EndVertexIndex);
            left <<= 32;
            long right = Math.Min(StartVertexIndex, EndVertexIndex);
            return (left | right);
        }
    }

    public XbimPackedNormal PackedNormal
    {
        get
        {
            return new XbimPackedNormal(Normal.X,Normal.Y,Normal.Z);
        }
    }
}
