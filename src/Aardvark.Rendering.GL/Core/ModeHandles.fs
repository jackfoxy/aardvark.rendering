﻿namespace Aardvark.Rendering.GL

open System
open System.Threading
open System.Collections.Concurrent
open System.Runtime.InteropServices
open Aardvark.Base
open Aardvark.Base.Rendering
open Microsoft.FSharp.NativeInterop
open OpenTK
open OpenTK.Platform
open OpenTK.Graphics
open OpenTK.Graphics.OpenGL4

#nowarn "9"

[<StructLayout(LayoutKind.Sequential)>]
type BeginMode =
    struct
        val mutable public Mode : int
        val mutable public PatchVertices : int

        new(m,v) = { Mode = m; PatchVertices = v }
    end

[<StructLayout(LayoutKind.Sequential)>]
type DrawCallInfoList =
    struct
        val mutable public Count : int64
        val mutable public Infos : nativeptr<DrawCallInfo>

        new(c : int, i) = { Count = int64 c; Infos = i }
    end

[<StructLayout(LayoutKind.Sequential)>]
type GLBlendMode =
    struct
        val mutable public Enabled : int
        val mutable public SourceFactor : int
        val mutable public DestFactor : int
        val mutable public Operation : int
        val mutable public SourceFactorAlpha : int
        val mutable public DestFactorAlpha : int
        val mutable public OperationAlpha : int
    end

[<StructLayout(LayoutKind.Sequential)>]
type GLStencilMode =
    struct
        val mutable public Enabled : int
        val mutable public CmpFront : int
        val mutable public MaskFront : uint32
        val mutable public ReferenceFront : int
        val mutable public CmpBack : int
        val mutable public MaskBack : uint32
        val mutable public ReferenceBack : int
        val mutable public OpFrontSF : int
        val mutable public OpFrontDF : int
        val mutable public OpFrontPass : int
        val mutable public OpBackSF : int
        val mutable public OpBackDF : int
        val mutable public OpBackPass : int
    end

[<StructLayout(LayoutKind.Sequential)>]
type DrawCallInfoListHandle =
    struct
        val mutable public Pointer : nativeptr<DrawCallInfoList>

        member x.Count
            with get() = NativePtr.read<int64> (NativePtr.cast x.Pointer) |> int
            and set (v : int) = NativePtr.write (NativePtr.cast x.Pointer) (int64 v)

        member x.Infos
            with get() : nativeptr<DrawCallInfo> = NativeInt.read (NativePtr.toNativeInt x.Pointer + 8n)
            and set (v : nativeptr<DrawCallInfo>) = NativeInt.write (NativePtr.toNativeInt x.Pointer + 8n) v
    
        new(ptr) = { Pointer = ptr }
    end

[<StructLayout(LayoutKind.Sequential)>]
type DepthTestModeHandle =
    struct
        val mutable public Pointer : nativeptr<int>
        new(p) = { Pointer = p }
    end

[<StructLayout(LayoutKind.Sequential)>]
type CullModeHandle =
    struct
        val mutable public Pointer : nativeptr<int>
        new(p) = { Pointer = p }
    end

[<StructLayout(LayoutKind.Sequential)>]
type PolygonModeHandle =
    struct
        val mutable public Pointer : nativeptr<int>
        new(p) = { Pointer = p }
    end

[<StructLayout(LayoutKind.Sequential)>]
type BeginModeHandle =
    struct
        val mutable public Pointer : nativeptr<BeginMode>
        new(p) = { Pointer = p }
    end

[<StructLayout(LayoutKind.Sequential)>]
type IsActiveHandle =
    struct
        val mutable public Pointer : nativeptr<int>
        new(p) = { Pointer = p }
    end

[<StructLayout(LayoutKind.Sequential)>]
type BlendModeHandle =
    struct
        val mutable public Pointer : nativeptr<GLBlendMode>
        new(p) = { Pointer = p }
    end

[<StructLayout(LayoutKind.Sequential)>]
type StencilModeHandle =
    struct
        val mutable public Pointer : nativeptr<GLStencilMode>
        new(p) = { Pointer = p }
    end