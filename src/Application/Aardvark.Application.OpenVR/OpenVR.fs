﻿namespace Aardvark.Application.OpenVR

open System
open Valve.VR
open Aardvark.Base
open Aardvark.Base.Incremental
open System.Runtime.InteropServices
open Microsoft.FSharp.NativeInterop

#nowarn "9"

type VrDeviceType =
    | Other = 0
    | Hmd = 1
    | Controller = 2
    | TrackingReference = 3

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module internal VrDeviceType =
    let ofETrackedDeviceClass =
        LookupTable.lookupTable [
            ETrackedDeviceClass.Controller, VrDeviceType.Controller
            ETrackedDeviceClass.HMD, VrDeviceType.Hmd
            ETrackedDeviceClass.TrackingReference, VrDeviceType.TrackingReference

            ETrackedDeviceClass.DisplayRedirect, VrDeviceType.Other
            ETrackedDeviceClass.GenericTracker, VrDeviceType.Other
            ETrackedDeviceClass.Invalid, VrDeviceType.Other
        ]

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module internal Trafo =
    let flip = Trafo3d.FromBasis(V3d.IOO, V3d.OOI, -V3d.OIO, V3d.Zero)

    let ofHmdMatrix34 (x : HmdMatrix34_t) =
        let t = 
            M44f(
                x.m0, x.m4, x.m8,  0.0f,
                x.m1, x.m5, x.m9,  0.0f,
                x.m2, x.m6, x.m10, 0.0f,
                x.m3, x.m7, x.m11, 1.0f
            ) 

        let t = M44d.op_Explicit(t)
        Trafo3d(t,t.Inverse) * flip

    let ofHmdMatrix44 (x : HmdMatrix44_t) =
        let t = M44f(x.m0,x.m1,x.m2,x.m3,x.m4,x.m5,x.m6,x.m7,x.m8,x.m9,x.m10,x.m11,x.m12,x.m13,x.m14,x.m15) 
        let t = M44d.op_Explicit(t)
        Trafo3d(t,t.Inverse)


    let angularVelocity (v : HmdVector3_t) =
        let v = V3d(-v.v0, -v.v1, -v.v2) // transposed world
        flip.Forward.TransformDir v


    let velocity (v : HmdVector3_t) =
        let v = V3d(v.v0, v.v1, v.v2)
        flip.Forward.TransformDir v

    let inline inverse (t : Trafo3d) = t.Inverse

type MotionState() =
    let isValid = Mod.init false
    let pose = Mod.init Trafo3d.Identity
    let angularVelocity = Mod.init V3d.Zero
    let velocity = Mod.init V3d.Zero

    member x.IsValid = isValid :> IMod<_>
    member x.Pose = pose :> IMod<_>
    member x.Velocity = velocity :> IMod<_>
    member x.AngularVelocity = angularVelocity :> IMod<_>

    member internal x.Update(newPose : byref<TrackedDevicePose_t>) =
        if newPose.bDeviceIsConnected && newPose.bPoseIsValid && newPose.eTrackingResult = ETrackingResult.Running_OK then
            let t = Trafo.ofHmdMatrix34 newPose.mDeviceToAbsoluteTracking
            isValid.Value <- true
            pose.Value <- t
            angularVelocity.Value <- Trafo.angularVelocity newPose.vAngularVelocity
            velocity.Value <- Trafo.velocity newPose.vVelocity
        else
            isValid.Value <- false

type VrDevice(system : CVRSystem, deviceType : VrDeviceType, index : int) =
    
    let getString (prop : ETrackedDeviceProperty) =
        let builder = System.Text.StringBuilder(4096, 4096)
        let mutable err = ETrackedPropertyError.TrackedProp_Success
        let len = system.GetStringTrackedDeviceProperty(uint32 index, prop, builder, uint32 builder.Capacity, &err)
        builder.ToString()

    let getInt (prop : ETrackedDeviceProperty) =
        let mutable err = ETrackedPropertyError.TrackedProp_Success
        let len = system.GetInt32TrackedDeviceProperty(uint32 index, prop, &err)

        len

    let vendor  = lazy ( getString ETrackedDeviceProperty.Prop_ManufacturerName_String )
    let model   = lazy ( getString ETrackedDeviceProperty.Prop_ModelNumber_String )
    
    let axis = 
        [|
            if deviceType = VrDeviceType.Controller then
                for i in 0 .. 4 do
                    let t = getInt (ETrackedDeviceProperty.Prop_Axis0Type_Int32 + unbox i) |> unbox<EVRControllerAxisType>
                    if t <> EVRControllerAxisType.k_eControllerAxis_None then
                        yield VrAxis(system, t, index, i)
        |]

    let axisByIndex =
        axis |> Seq.map (fun a -> a.Index, a) |> Map.ofSeq

    let events = Event<VREvent_t>()

    let state = MotionState()

    member x.Axis = axis

    member x.Type = deviceType

    member x.MotionState = state

    member x.Events = events.Publish

    member internal x.Update(poses : TrackedDevicePose_t[]) =
        state.Update(&poses.[index])

        let mutable state = VRControllerState_t()
        if system.GetControllerState(uint32 index, &state, uint32 sizeof<VRControllerState_t>) then
            for a in axis do a.Update(&state)

    member internal x.Trigger(evt : byref<VREvent_t>) =
        let buttonIndex = int evt.data.controller.button |> unbox<EVRButtonId>
        if buttonIndex >= EVRButtonId.k_EButton_Axis0 && buttonIndex <= EVRButtonId.k_EButton_Axis4 then
            let ai = buttonIndex - EVRButtonId.k_EButton_Axis0 |> int
            match Map.tryFind ai axisByIndex with
                | Some a -> a.Trigger(&evt)
                | None -> ()

        events.Trigger(evt)

and VrAxis(system : CVRSystem, axisType : EVRControllerAxisType, deviceIndex : int, axisIndex : int) =
    let touched = Mod.init false
    let pressed = Mod.init false
    let position = Mod.init None
    
    let touch = Event<unit>()
    let untouch = Event<unit>()
    let press = Event<unit>()
    let unpress = Event<unit>()

    member x.Touched = touched :> IMod<_>
    member x.Pressed = pressed :> IMod<_>
    member x.Position = position :> IMod<_>
    member x.Touch = touch.Publish
    member x.UnTouch = untouch.Publish
    member x.Press = press.Publish
    member x.UnPress = unpress.Publish

    member x.Index = axisIndex

    member internal x.Update(s : byref<VRControllerState_t>) =
        if touched.Value then
            let pos : VRControllerAxis_t =
                match axisIndex with
                    | 0 -> s.rAxis0
                    | 1 -> s.rAxis1
                    | 2 -> s.rAxis2
                    | 3 -> s.rAxis3
                    | 4 -> s.rAxis4
                    | _ -> raise <| System.IndexOutOfRangeException()
                
            transact (fun () ->
                position.Value <- Some (V2d(pos.x, pos.y))
            )
        else
            match position.Value with
                | Some _ -> transact (fun () -> position.Value <- None)
                | _ -> ()
        ()

    member internal x.Trigger(e : byref<VREvent_t>) =
        let eventType = e.eventType |> int |> unbox<EVREventType>
        transact (fun () ->
            match eventType with
                | EVREventType.VREvent_ButtonTouch -> 
                    touch.Trigger()
                    touched.Value <- true
                | EVREventType.VREvent_ButtonUntouch -> 
                    untouch.Trigger()
                    touched.Value <- false
                | EVREventType.VREvent_ButtonPress -> 
                    press.Trigger()
                    pressed.Value <- true
                | EVREventType.VREvent_ButtonUnpress -> 
                    unpress.Trigger()
                    pressed.Value <- false
                | _ -> ()
        )


type VrTexture =
    class
        val mutable public Data : nativeint
        val mutable public Info : Texture_t
        val mutable public Flags : EVRSubmitFlags
        val mutable public Bounds : VRTextureBounds_t

        new(d,i,f,b) = { Data = d; Info = i; Flags = f; Bounds = b }

        static member OpenGL(handle : int) =
            let i = Texture_t(eColorSpace = EColorSpace.Auto, eType = ETextureType.OpenGL, handle = nativeint handle)
            let b = VRTextureBounds_t(uMin = 0.0f, uMax = 1.0f, vMin = 0.0f, vMax = 1.0f)
            new VrTexture(0n, i, EVRSubmitFlags.Submit_Default, b)

        static member Vulkan(data : VRVulkanTextureData_t) =
            let ptr = Marshal.AllocHGlobal sizeof<VRVulkanTextureData_t>
            NativeInt.write ptr data
            let i = Texture_t(eColorSpace = EColorSpace.Auto, eType = ETextureType.Vulkan, handle = ptr)
            let b = VRTextureBounds_t(uMin = 0.0f, uMax = 1.0f, vMin = 0.0f, vMax = 1.0f)
            new VrTexture(ptr, i, EVRSubmitFlags.Submit_Default, b)
            
        static member D3D12(data : D3D12TextureData_t) =
            let ptr = Marshal.AllocHGlobal sizeof<D3D12TextureData_t>
            NativeInt.write ptr data
            let i = Texture_t(eColorSpace = EColorSpace.Auto, eType = ETextureType.DirectX12, handle = ptr)
            let b = VRTextureBounds_t(uMin = 0.0f, uMax = 1.0f, vMin = 0.0f, vMax = 1.0f)
            new VrTexture(ptr, i, EVRSubmitFlags.Submit_Default, b)
            
        member x.Dispose() =
            if x.Data <> 0n then Marshal.FreeHGlobal x.Data

        interface IDisposable with
            member x.Dispose() = x.Dispose()

    end

type VrRenderInfo =
    {
        framebufferSize     : V2i
        viewTrafo           : IMod<Trafo3d>
        lProjTrafo          : IMod<Trafo3d>
        rProjTrafo          : IMod<Trafo3d>
    }

[<AbstractClass>]
type VrRenderer() =
    let system =
        let mutable err = EVRInitError.None
        let sys = OpenVR.Init(&err)
        if err <> EVRInitError.None then
            Log.error "[OpenVR] %s" (OpenVR.GetStringForHmdError err)
            failwithf "[OpenVR] %s" (OpenVR.GetStringForHmdError err)
        sys
    
    let devicesPerIndex =
        [|
            for i in 0u .. OpenVR.k_unMaxTrackedDeviceCount-1u do
                let deviceType = system.GetTrackedDeviceClass i
                if deviceType <> ETrackedDeviceClass.Invalid then
                    yield VrDevice(system, VrDeviceType.ofETrackedDeviceClass deviceType, int i) |> Some
                else
                    yield None
        |]

    let devices = devicesPerIndex |> Array.choose id
        

    let hmds = devices |> Array.filter (fun d -> d.Type = VrDeviceType.Hmd)
    let controllers = devices |> Array.filter (fun d -> d.Type = VrDeviceType.Controller)
    
    let compositor = OpenVR.Compositor
    let renderPoses = Array.zeroCreate (int OpenVR.k_unMaxTrackedDeviceCount)
    let gamePoses = Array.zeroCreate (int OpenVR.k_unMaxTrackedDeviceCount)

    [<VolatileField>]
    let mutable isAlive = true

    let check (str : string) (err : EVRCompositorError) =
        if err <> EVRCompositorError.None then
            Log.error "[OpenVR] %A: %s" err str
            failwithf "[OpenVR] %A: %s" err str

    let depthRange = Range1d(0.1, 100.0) |> Mod.init

    let lProj =
        let headToEye = system.GetEyeToHeadTransform(EVREye.Eye_Left) |> Trafo.ofHmdMatrix34 |> Trafo.inverse
        depthRange |> Mod.map (fun range ->
            let proj = system.GetProjectionMatrix(EVREye.Eye_Left, float32 range.Min, float32 range.Max)
            headToEye * Trafo.ofHmdMatrix44 proj
        )

    let rProj =
        let headToEye = system.GetEyeToHeadTransform(EVREye.Eye_Right) |> Trafo.ofHmdMatrix34 |> Trafo.inverse
        depthRange |> Mod.map (fun range ->
            let proj = system.GetProjectionMatrix(EVREye.Eye_Left, float32 range.Min, float32 range.Max)
            headToEye * Trafo.ofHmdMatrix44 proj
        )

    let desiredSize =
        let mutable width = 0u
        let mutable height = 0u
        system.GetRecommendedRenderTargetSize(&width,&height)
        V2i(int width, int height)


    let infos =
        hmds |> Array.map (fun hmd ->
            {
                framebufferSize = desiredSize
                viewTrafo = hmd.MotionState.Pose |> Mod.map Trafo.inverse
                lProjTrafo = lProj
                rProjTrafo = rProj
            }
        )

    let processEvents() =
        let mutable evt : VREvent_t = Unchecked.defaultof<VREvent_t>
        while system.PollNextEvent(&evt, uint32 sizeof<VREvent_t>) do
            let id = evt.trackedDeviceIndex |> int
            if id >= 0 && id < devicesPerIndex.Length then
                match devicesPerIndex.[id] with
                    | Some device ->
                        device.Trigger(&evt)

                    | None ->
                        ()

            

    member x.DesiredSize = desiredSize

    member x.Shutdown() =
        isAlive <- false

    abstract member OnLoad : info : VrRenderInfo -> VrTexture * VrTexture
    abstract member Render : unit -> unit
    abstract member Release : unit -> unit

    member x.Run (render : VrRenderInfo * VrRenderInfo -> VrTexture * VrTexture) =
        if not isAlive then raise <| ObjectDisposedException("VrSystem")
        let (lTex, rTex) = x.OnLoad infos.[0] 

        while isAlive do
            processEvents()

            let err = compositor.WaitGetPoses(renderPoses, gamePoses)
            if err = EVRCompositorError.None then

                // update all poses
                transact (fun () ->
                    for d in devices do d.Update(renderPoses)
                )
            
                // render for all HMDs
                for i in 0 .. hmds.Length - 1 do
                    let hmd = hmds.[i]
                     
                    if hmd.MotionState.IsValid.GetValue() then
                        x.Render()

                        compositor.Submit(EVREye.Eye_Left, &lTex.Info, &lTex.Bounds, lTex.Flags) |> check "submit left"
                        compositor.Submit(EVREye.Eye_Right, &rTex.Info, &rTex.Bounds, rTex.Flags) |> check "submit right"

            else
                Log.error "[OpenVR] %A" err
        
        lTex.Dispose()
        rTex.Dispose()
        x.Release()
        OpenVR.Shutdown()

    member x.Hmd = hmds.[0]

    member x.Controllers = controllers

