﻿namespace Aardvark.Base.Rendering

open Aardvark.Base
open Aardvark.Base.Incremental
open FShade
open Microsoft.FSharp.Quotations
open DefaultSurfaceVertex

module NormalMap =
 
    let private normalSampler =
        sampler2d {
            texture uniform?NormalMapTexture
            filter Filter.MinMagMipLinear
            addressU WrapMode.Wrap
            addressV WrapMode.Wrap
        }

    let normalMap (v : Vertex) =
        fragment {
            let texColor = normalSampler.Sample(v.tc).XYZ
            let texNormal = (2.0 * texColor - V3d.III) |> Vec.normalize


            let n = v.n.Normalized * texNormal.Z + v.b.Normalized * texNormal.X + v.t.Normalized * texNormal.Y |> Vec.normalize

            return { v with n = n }
        }

    let Effect = 
        toEffect normalMap

