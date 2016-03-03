﻿namespace Aardvark.SceneGraph

open System
open Aardvark.Base
open Aardvark.Base.Rendering
open Aardvark.Base.Incremental

type PointCloudInfo =
    {
        /// the element-types for all available attributes
        attributeTypes : Map<Symbol, Type>

        /// the target point distance in pixels
        targetPointDistance : IMod<float>

        /// an optional custom view trafo
        customView : Option<IMod<Trafo3d>>

        /// an optional custom view projection trafo
        customProjection : Option<IMod<Trafo3d>>

        /// the maximal percentage of inactive vertices kept in memory
        /// For Example a value of 0.5 means that at most 50% of the vertices in memory are inactive
        maxReuseRatio : float

        /// the minimal number of inactive vertices kept in memory
        minReuseCount : int64

        /// the time interval for the pruning process in ms
        pruneInterval : int

    }

[<AutoOpen>]
module ``PointCloud Sg Extensions`` =

    module Sg = 
        type PointCloud(data : ILodData, config : PointCloudInfo) =
            interface ISg

            member x.Data = data
            member x.Config = config

        let pointCloud (data : ILodData) (info : PointCloudInfo) =
            PointCloud(data, info) :> ISg
            

namespace Aardvark.SceneGraph.Semantics

open System
open System.Threading
open System.Threading.Tasks
open Aardvark.Base
open Aardvark.Base.Ag
open Aardvark.Base.Rendering
open Aardvark.Base.Incremental
open Aardvark.SceneGraph


module PointCloudRenderObjectSemantics = 

    [<CustomEquality; NoComparison>]
    type GeometryRef = { node : LodDataNode; geometry : IndexedGeometry; range : Range1i } with

        override x.GetHashCode() = HashCode.Combine(x.node.GetHashCode(), x.range.GetHashCode())
        override x.Equals o =
            match o with
                | :? GeometryRef as o -> 
                    x.node.Equals(o.node) && x.range = o.range
                | _ -> false

    type LoadTask(factory : TaskFactory, run : Async<GeometryRef>, ct : CancellationToken, activate : GeometryRef -> unit, deactivate : GeometryRef -> unit) =
        let cancel = new CancellationTokenSource()
        let mutable killed = 0
        let mutable refCnt = 1
        let mutable running = true

        let task = factory.StartNew(fun () ->
            try
                let res = Async.RunSynchronously(run, cancellationToken = cancel.Token)
                running <- false

                let r = refCnt
                if r > 0 then 
                    activate res
                    Some res
                else
                    deactivate res
                    Some res
            with :? OperationCanceledException ->
                printfn "killed"
                None
        )

        member x.Deactivate() =
            if killed = 1 then failwith "cannot deactive killed LoadTask"

            let newCnt = Interlocked.Decrement(&refCnt)
            if newCnt = 0 then
                if not running then 
                    match task.Result with
                        | Some res -> deactivate res
                        | _ -> ()


        member x.Activate() =
            if killed = 1 then failwith "cannot active killed LoadTask"

            let newCnt = Interlocked.Increment(&refCnt)
            if newCnt = 1 then
                if not running then 
                    match task.Result with
                        | Some res -> activate res
                        | _ -> ()

        member x.Kill(cont : GeometryRef -> unit) =
            if Interlocked.Exchange(&killed, 1) = 0 then
                cancel.Cancel()

                let killNow (t : Task<Option<GeometryRef>>) =
                    match t.Result with
                        | Some res -> cont res
                        | None -> ()

                if running then task.ContinueWith killNow |> ignore
                else killNow task

    type LoadTaskasdasd(factory : TaskFactory, run : Async<GeometryRef>, ct : CancellationToken, activate : GeometryRef -> unit, deactivate : GeometryRef -> unit) =
        let r = Async.RunSynchronously run
        do activate r
        let mutable refCnt = 1
        member x.Deactivate() = 
            if Interlocked.Decrement(&refCnt) = 0 then
                deactivate r

        member x.Activate() = 
            if Interlocked.Increment(&refCnt) = 1 then
                activate r

        member x.Kill cont = cont r

    type PointCloudHandler(node : Sg.PointCloud, view : IMod<Trafo3d>, proj : IMod<Trafo3d>, viewportSize : IMod<V2i>, runtime : IRuntime) =
        let cancel = new System.Threading.CancellationTokenSource()

        let pool = GeometryPool.createAsync runtime
        let calls = DrawCallSet(true)
        let inactive = ConcurrentHashQueue<GeometryRef>()
        let mutable inactiveSize = 0L
        let mutable activeSize = 0L
        let mutable activeCount = 0
        let mutable desiredCount = 0
        let scheduler = new Aardvark.Base.CustomTaskScheduler(8, ThreadPriority.AboveNormal)
        let factory = new TaskFactory(scheduler)

        let geometriesRW = new ReaderWriterLockSlim()
        let geometries = Dict<LodDataNode, LoadTask>()

        let activate (n : GeometryRef) =
            let size = n.range.Size

            if inactive.Remove n then
                Interlocked.Add(&inactiveSize, int64 -size) |> ignore

            if calls.Add n.range then
                Interlocked.Add(&activeSize, int64 size) |> ignore
                Interlocked.Increment(&activeCount) |> ignore

        let deactivate (n : GeometryRef) =
            let size = int64 n.range.Size

            if inactive.Enqueue n then
                Interlocked.Add(&inactiveSize, size) |> ignore

            if calls.Remove n.range then
                Interlocked.Add(&activeSize, -size) |> ignore
                Interlocked.Decrement(&activeCount) |> ignore


        let loadTask (a : Async<GeometryRef>) =
            new LoadTask(factory, a, cancel.Token, activate, deactivate)

        member x.Add(n : LodDataNode) =
            Interlocked.Increment(&desiredCount) |> ignore
            let isNew = ref false
            let result = 
                ReaderWriterLock.write geometriesRW (fun () ->
                    geometries.GetOrCreate(n, fun n ->
                        isNew := true
                        async {
                            let! g = node.Data.GetData n
                            return
                                let range = pool.Add(g) 
                                in { node = n; geometry = g; range = range }
                        } |> loadTask
                    )
                )

            if not !isNew then
                result.Activate()

            result

        member x.Remove(n : LodDataNode) =
            Interlocked.Decrement(&desiredCount) |> ignore
            ReaderWriterLock.read geometriesRW (fun () ->
                match geometries.TryGetValue n with
                    | (true, t) -> 
                        t.Deactivate() 
                    | _ -> ()
            )


        member x.Activate() =
                
            let wantedNearPlaneDistance =
                Mod.custom (fun self ->
                    let viewportSize = viewportSize.GetValue self
                    let wantedPixelDistance = node.Config.targetPointDistance.GetValue self

                    let size = max viewportSize.X viewportSize.Y
                    2.0 * float wantedPixelDistance / float size
                )

            let content =
                ASet.custom (fun self ->
                    let view = view.GetValue self
                    let proj = proj.GetValue self
                    let wantedNearPlaneDistance = wantedNearPlaneDistance.GetValue self

                    let set = node.Data.Rasterize(view, proj, wantedNearPlaneDistance)

                    let add = set |> Seq.filter (self.Content.Contains >> not) |> Seq.map Add
                    let rem = self.Content |> Seq.filter (set.Contains >> not) |> Seq.map Rem

                    let res = Seq.append add rem |> Seq.toList
                    res
                )


            let deltas = ConcurrentDeltaQueue.ofASet content

            let deltaProcessing =
                async {
                    while true do
                        let! op = deltas.DequeueAsync()

                        match op with
                            | Add n -> x.Add n |> ignore
                            | Rem n -> x.Remove n |> ignore


                }

            let pruning =
                async {
                    while true do
                        let mutable cnt = 0

                        let shouldContinue () =
                            if inactiveSize > node.Config.minReuseCount then 
                                let ratio = float inactiveSize / float (inactiveSize + activeSize)
                                if ratio > node.Config.maxReuseRatio then true
                                else false
                            else
                                false

                        while shouldContinue () do
                            ReaderWriterLock.write geometriesRW (fun () ->
                                match inactive.TryDequeue() with
                                    | (true, v) ->
                                            match geometries.TryRemove v.node with
                                                | (true, v) ->
                                                    v.Kill (fun v ->
                                                        let r = pool.Remove v.geometry
                                                        Interlocked.Add(&inactiveSize, int64 -v.range.Size) |> ignore
                                                    )
                                                    cnt <- cnt + 1
                                                | _ ->
                                                    Log.warn "failed to remove node: %A" v.node.id
                                    | _ -> Log.warn "strange"
                           )
                            
                        do! Async.Sleep node.Config.pruneInterval
                }

            let printer =
                async {
                    while true do
                        do! Async.Sleep(1000)
                        printfn "%A / %A / %A" activeCount desiredCount geometries.Count
                        ()
                }


            Async.StartAsTask(deltaProcessing, cancellationToken = cancel.Token) |> ignore
            Async.StartAsTask(pruning, cancellationToken = cancel.Token) |> ignore
            Async.StartAsTask(printer, cancellationToken = cancel.Token) |> ignore

            { new IDisposable with member x.Dispose() = () }


        member x.Attributes =
            { new IAttributeProvider with
                member x.TryGetAttribute sem =
                    match Map.tryFind sem node.Config.attributeTypes with
                        | Some t ->
                            let b = pool.GetBuffer sem
                            BufferView(b, t) |> Some
                        | None ->
                            None

                member x.All = Seq.empty
                member x.Dispose() = ()
            }

        member x.DrawCallInfos =
            calls |> DrawCallSet.toMod

    [<Semantic>]
    type PointCloudSemantics() =
        member x.RenderObjects(l : Sg.PointCloud) =
            let obj = RenderObject.create()

            let view = 
                match l.Config.customView with
                    | Some v -> v
                    | None -> l.ViewTrafo

            let proj = 
                match l.Config.customProjection with
                    | Some p -> p
                    | None -> l.ProjTrafo

            let viewportSize =
                match obj.Uniforms.TryGetUniform(obj.AttributeScope, Symbol.Create "ViewportSize") with
                    | Some (:? IMod<V2i> as vs) -> vs
                    | _ -> failwith "[PointCloud] could not get viewport size (please apply to scenegraph)"

            let h = PointCloudHandler(l, view, proj, viewportSize, l.Runtime)

            let calls = h.DrawCallInfos

            obj.IndirectBuffer <- calls |> Mod.map (fun a -> ArrayBuffer(a) :> IBuffer)
            //obj.DrawCallInfos <- calls |> Mod.map Array.toList
            obj.Activate <- h.Activate
            obj.VertexAttributes <- h.Attributes
            obj.Mode <- Mod.constant IndexedGeometryMode.PointList

            ASet.single (obj :> IRenderObject)

