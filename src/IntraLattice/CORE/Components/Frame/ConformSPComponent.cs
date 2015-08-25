﻿using System;
using System.Collections.Generic;

using Grasshopper.Kernel;
using Rhino.Geometry;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using IntraLattice.Properties;
using Grasshopper;
using IntraLattice.CORE.Data;
using Rhino.Collections;
using IntraLattice.CORE.Helpers;

// Summary:     This component generates a (u,v,w) lattice between a surface and a point.
// ===============================================================================
// Details:     - Surface does not need to be closed, but it can be.
//              - Point does not need to be centered with respect to the surface.
// ===============================================================================
// Author(s):   Aidan Kurtz (http://aidankurtz.com)

namespace IntraLattice.CORE.Components
{
    public class ConformSP : GH_Component
    {
        /// <summary>
        /// Initializes a new instance of the ConformSP class.
        /// </summary>
        public ConformSP()
            : base("Conform Surface-Point", "ConformSP",
                "Generates a conforming lattice between a surface and a point.",
                "IntraLattice2", "Frame")
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("Topology", "Topo", "Unit cell topology", GH_ParamAccess.item);
            pManager.AddSurfaceParameter("Surface", "Surf", "Surface to conform to", GH_ParamAccess.item);
            pManager.AddPointParameter("Point", "Pt", "Point", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Number u", "Nu", "Number of unit cells (u)", GH_ParamAccess.item, 5);
            pManager.AddIntegerParameter("Number v", "Nv", "Number of unit cells (v)", GH_ParamAccess.item, 5);
            pManager.AddIntegerParameter("Number w", "Nw", "Number of unit cells (w)", GH_ParamAccess.item, 5);
            pManager.AddBooleanParameter("Morph", "Morph", "If true, struts are morphed to the space as curves.", GH_ParamAccess.item, false);
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter("Struts", "Struts", "Strut curve network", GH_ParamAccess.list);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // 1. Retrieve and validate inputs
            var cell = new UnitCell();
            Surface surface = null;
            Point3d pt = Point3d.Unset;
            int nU = 0;
            int nV = 0;
            int nW = 0;
            bool morphed = false;

            if (!DA.GetData(0, ref cell)) { return; }
            if (!DA.GetData(1, ref surface)) { return; }
            if (!DA.GetData(2, ref pt)) { return; }
            if (!DA.GetData(3, ref nU)) { return; }
            if (!DA.GetData(4, ref nV)) { return; }
            if (!DA.GetData(5, ref nW)) { return; }
            if (!DA.GetData(6, ref morphed)) { return; }

            if (!cell.isValid) { return; }
            if (!surface.IsValid) { return; }
            if (!pt.IsValid) { return; }
            if (nU == 0) { return; }
            if (nV == 0) { return; }
            if (nW == 0) { return; }

            // 2. Initialize the node tree, derivative tree and morphed space tree
            var lattice = new Lattice();
            var spaceTree = new DataTree<GeometryBase>(); // will contain the morphed uv spaces (as surface-surface, surface-axis or surface-point)

            // 3. Package the number of cells in each direction into an array
            float[] N = new float[3] { nU, nV, nW };

            // 4. Normalize the UV-domain
            Interval unitDomain = new Interval(0, 1);
            surface.SetDomain(0, unitDomain); // surface u-direction
            surface.SetDomain(1, unitDomain); // surface v-direction

            // 5. Prepare normalized/formatted unit cell topology
            cell = cell.Duplicate();
            cell.FormatTopology();          // sets up paths for inter-cell nodes

            // 6. Let's create the actual lattice nodes now
            //
            for (int u = 0; u <= N[0]; u++)
            {
                for (int v = 0; v <= N[1]; v++)
                {
                    for (int w = 0; w <= N[2]; w++)
                    {
                        GH_Path treePath = new GH_Path(u, v, w);                // construct cell path in tree
                        var nodeList = lattice.Nodes.EnsurePath(treePath);      // fetch the list of nodes to append to, or initialise it

                        // this loop maps each node in the cell onto the UV-surface map and axis (U)
                        for (int i = 0; i < cell.Nodes.Count; i++)
                        {
                            double usub = cell.Nodes[i].X; // u-position within unit cell (local)
                            double vsub = cell.Nodes[i].Y; // v-position within unit cell (local)
                            double wsub = cell.Nodes[i].Z; // w-position within unit cell (local)
                            double[] uvw = { u + usub, v + vsub, w + wsub }; // uvw-position (global)

                            // check if the node belongs to another cell (i.e. it's relative path points outside the current cell)
                            bool isOutsideCell = (cell.NodePaths[i][0] > 0 || cell.NodePaths[i][1] > 0 || cell.NodePaths[i][2] > 0);
                            // check if current uvw-position is beyond the upper boundary
                            bool isOutsideSpace = (uvw[0] > N[0] || uvw[1] > N[1] || uvw[2] > N[2]);

                            if (isOutsideCell || isOutsideSpace)
                                nodeList.Add(null);
                            else
                            {
                                // evaluate the point on the axis
                                Point3d pt1 = pt;
                                Point3d pt2; Vector3d[] derivatives; // initialize for surface 2

                                // evaluate point and its derivatives on the surface
                                surface.Evaluate(uvw[0] / N[0], uvw[1] / N[1], 2, out pt2, out derivatives);

                                // create vector joining the two points (this is our w-range)
                                Vector3d wVect = pt2 - pt1;

                                // create the node, accounting for the position along the w-direction
                                var newNode = new LatticeNode(pt1 + wVect * uvw[2] / N[2]);
                                nodeList.Add(newNode);
                            }

                        }
                    }

                    // Define the uv space map
                    if (morphed && u < N[0] && v < N[1])
                    {
                        GH_Path spacePath = new GH_Path(u, v);
                        var uInterval = new Interval((u) / N[0], (u + 1) / N[0]);           // set trimming interval
                        var vInterval = new Interval((v) / N[1], (v + 1) / N[1]);
                        Surface ss1 = surface.Trim(uInterval, vInterval);                   // create sub-surface
                        Point ss2 = new Point(pt);                                          // point never changes
                        ss1.SetDomain(0, unitDomain); ss1.SetDomain(1, unitDomain);         // normalize domain
                        // Save to the space tree
                        spaceTree.Add(ss1, spacePath);
                        spaceTree.Add(ss2, spacePath);
                    }
                }
            }

            // 7. Generate the struts
            //    Simply loop through all unit cells, and enforce the cell topology (using cellStruts: pairs of node indices)
            if (morphed) lattice.MorphMapping(cell, spaceTree, N);
            else lattice.ConformMapping(cell, N);

            // 8. Set output
            DA.SetDataList(0, lattice.Struts);
        }

        // Conform components are in second slot of the grid category
        public override GH_Exposure Exposure
        {
            get
            {
                return GH_Exposure.secondary;
            }
        }

        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                //You can add image files to your project resources and access them like this:
                //return Resources.circle4;
                return null;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("{27cbc46a-3ef6-4f00-9a66-d6afd6b7b2fe}"); }
        }
    }
}