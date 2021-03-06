﻿using System;
using System.Runtime.InteropServices;
using SlimDX;
using SlimDX.D3DCompiler;
using SlimDX.Direct3D10;
using SlimDX.DXGI;
using Buffer = SlimDX.Direct3D10.Buffer;
using Device = SlimDX.Direct3D10.Device;
using MapFlags = SlimDX.Direct3D10.MapFlags;

namespace SceneViewerWPF
{

    public interface IKinectPointsCloudRenderer : IRenderer
    {
        void Init(KinectFrame frame, KinectCameraInfo cameraInfo);
        void Update(KinectFrame frame, KinectCameraInfo cameraInfo);

        float Scale { get; set; }
        Vector4 FillColor { get; set; }
        DxLight Light { get; set; }
    }

    public interface IRenderer : IDisposable
    {
        void Update(float dt, float time);
        void Render(DxCamera camera);
    }


    public enum KinectPointsRendererType
    {
        Billboard,
        Mesh
    }

    public class DxKinectPointsCloudRenderer : IKinectPointsCloudRenderer
    {
        private readonly Device _dxDevice;

        private bool _initialized;
        private int _xRes;
        private int _yRes;
        
        private int _vertexCount;
        private Buffer _vertexBuffer;

        private Effect _effect;
        private EffectTechnique _renderTech;
        private InputLayout _inputLayout;
        
        private EffectVectorVariable _eyePosWVar;
        private EffectMatrixVariable _viewProjVar;
        private EffectMatrixVariable _worldVar;

        private Texture2D _imageTexture;
        private ShaderResourceView _imageTextureRV;
        private EffectResourceVariable _imageMapVar;

        private Buffer _depthMapBuffer;
        private ShaderResourceView _depthMapBufferRV;
        private EffectResourceVariable _depthMapVar;

        private float _vertexScale = 0.1f; // Scale from mm to cm!

        private float _focalLengthDepth;
        private float _focalLengthImage;

        private EffectScalarVariable _focalLengthDepthVar;
        private EffectScalarVariable _focalLengthImageVar;
        private EffectVectorVariable _resVar;

        private Vector4 _fillColor;
        private EffectVectorVariable _fillColorVar;

        private Matrix _depthToRgb;
        private EffectMatrixVariable _depthToRgbVar;
        private DxLight _light;

        [StructLayout(LayoutKind.Sequential)]
        private struct PointVertex
        {
            public short X;
            public short Y;

            public PointVertex(short x, short y)
            {
                X = x;
                Y = y;
            }

            public static int SizeOf
            {
                get { return Marshal.SizeOf(typeof(PointVertex)); }
            }
        }

        public float Scale
        {
            get { return _vertexScale;  }
            set { _vertexScale = value; }
        }

        public Vector4 FillColor
        {
            get { return _fillColor; }
            set { _fillColor = value; }
        }

        public DxLight Light
        {
            get { return _light; }
            set { _light = value; }
        }

        public DxKinectPointsCloudRenderer(Device dxDevice)
        {
            _dxDevice = dxDevice;

            LoadEffect(@"Assets\kinectpoints_simple.fx");
        }

        private void LoadEffect(string shaderFileName)
        {
            _effect = Effect.FromFile(_dxDevice, shaderFileName, "fx_4_0",
                                      ShaderFlags.None, EffectFlags.None, null, null);

            _renderTech = _effect.GetTechniqueByName("Render"); 

            _eyePosWVar = _effect.GetVariableByName("gEyePosW").AsVector();
            _viewProjVar = _effect.GetVariableByName("gViewProj").AsMatrix();
            _worldVar = _effect.GetVariableByName("gWorld").AsMatrix();
            _fillColorVar = _effect.GetVariableByName("gFillColor").AsVector();
            
            _imageMapVar = _effect.GetVariableByName("gImageMap").AsResource();
            _depthMapVar = _effect.GetVariableByName("gDepthMap").AsResource();

            _resVar = _effect.GetVariableByName("gRes").AsVector();
            _depthToRgbVar = _effect.GetVariableByName("gDepthToRgb").AsMatrix();
            _focalLengthDepthVar = _effect.GetVariableByName("gFocalLengthDepth").AsScalar();
            _focalLengthImageVar = _effect.GetVariableByName("gFocalLengthImage").AsScalar();

            ShaderSignature signature = _renderTech.GetPassByIndex(0).Description.Signature;
            _inputLayout = new InputLayout(_dxDevice, signature,
                    new[] { new InputElement("POSITION", 0, SlimDX.DXGI.Format.R16G16_SInt, 0, 0)
                });
        }

        public void Init(KinectFrame frame, KinectCameraInfo cameraInfo)
        {
            _initialized = true;

            // store variables
            _xRes = cameraInfo.XRes;
            _yRes = cameraInfo.YRes;

            _focalLengthImage = (float)cameraInfo.FocalLengthImage;
            _focalLengthDepth = (float)cameraInfo.FocalLengthDetph;
            _depthToRgb = cameraInfo.DepthToRgb;

            CreateVertexBuffer();
            CreateTextures();
        }

        private void CreateTextures()
        {
            // RGB image texture goes here
            var descTex = new Texture2DDescription
                              {
                                  BindFlags = BindFlags.ShaderResource,
                                  CpuAccessFlags = CpuAccessFlags.Write,
                                  Format = SlimDX.DXGI.Format.R8G8B8A8_UNorm,
                                  //Format = SlimDX.DXGI.Format.R32G32B32A32_Float,
                                  OptionFlags = ResourceOptionFlags.None,
                                  SampleDescription = new SampleDescription(1, 0),
                                  Width = _xRes,
                                  Height = _yRes,
                                  MipLevels = 1,
                                  ArraySize = 1,
                                  Usage = ResourceUsage.Dynamic
                              };

            _imageTexture = new Texture2D(_dxDevice, descTex);

            _imageTextureRV = new ShaderResourceView(_dxDevice, _imageTexture);

            // depth map buffer
            _depthMapBuffer = new Buffer(_dxDevice, _vertexCount * sizeof(UInt16), ResourceUsage.Dynamic,
                BindFlags.ShaderResource, CpuAccessFlags.Write, ResourceOptionFlags.None);

            _depthMapBufferRV = new ShaderResourceView(_dxDevice, _depthMapBuffer,
                new ShaderResourceViewDescription
                {
                    Dimension = ShaderResourceViewDimension.Buffer,
                    Format = Format.R16_UInt,
                    ElementOffset = 0,
                    ElementWidth = _vertexCount,
                });
        }

        private void CreateVertexBuffer()
        {
            _vertexCount = _xRes * _yRes;

            using (var vertexStream = new DataStream(_vertexCount * PointVertex.SizeOf, true, true))
            {
                // store pixel coordinates in each vertex
                for (short y = 0; y < _yRes; y++)
                {
                    for (short x = 0; x < _xRes; x++)
                    {
                        var pt = new PointVertex(x, y);
                        vertexStream.Write(pt);
                    }
                }
                vertexStream.Position = 0;

                // create vertex buffer
                _vertexBuffer = new Buffer(_dxDevice, vertexStream,
                                           _vertexCount*PointVertex.SizeOf, ResourceUsage.Immutable,
                                           BindFlags.VertexBuffer,
                                           CpuAccessFlags.None, ResourceOptionFlags.None);
            }
        }

        public void Update(KinectFrame frame, KinectCameraInfo cameraInfo)
        {
            if (!_initialized)
                Init(frame, cameraInfo);

            var imageRect = _imageTexture.Map(0, MapMode.WriteDiscard, MapFlags.None);
            var imageMap = frame.ImageMap;
            var imagePtr = 0;
            
            // update texture
            // need to convert from RGB24 to RGBA32
            for (int v = 0; v < _yRes; v++)
            {
                for (int u = 0; u < _xRes; u++)
                {
                    byte r = imageMap[imagePtr++];
                    byte g = imageMap[imagePtr++];
                    byte b = imageMap[imagePtr++];
                    byte a = 255;

                    int argb = (a << 24) + (b << 16) + (g << 8) + r;
                    imageRect.Data.Write(argb);
                }
            }
            _imageTexture.Unmap(0);

            // update depth map
            DataStream depthStream = _depthMapBuffer.Map(MapMode.WriteDiscard, MapFlags.None);
            depthStream.WriteRange(frame.DepthMap);
            _depthMapBuffer.Unmap();
        }

        public void Update(float dt, float time)
        {
            // ignore
        }

        public void Render(DxCamera camera)
        {
            if (_vertexBuffer == null || _vertexCount == 0)
                return;

            _worldVar.SetMatrix(Matrix.Scaling(Scale, -Scale, Scale));
            _eyePosWVar.Set(camera.Eye);
            _viewProjVar.SetMatrix(camera.View*camera.Projection);
            _fillColorVar.Set(_fillColor);

            _resVar.Set(new Vector2(_xRes, _yRes));
            _focalLengthDepthVar.Set(_focalLengthDepth);
            _focalLengthImageVar.Set(_focalLengthImage);
            _depthToRgbVar.SetMatrix(_depthToRgb);

            _depthMapVar.SetResource(_depthMapBufferRV);
            _imageMapVar.SetResource(_imageTextureRV);

            _dxDevice.InputAssembler.SetInputLayout(_inputLayout);
            _dxDevice.InputAssembler.SetPrimitiveTopology(PrimitiveTopology.PointList);
            _dxDevice.InputAssembler.SetVertexBuffers(0, 
                new VertexBufferBinding(_vertexBuffer, PointVertex.SizeOf, 0));

            for (int p = 0; p < _renderTech.Description.PassCount; p++)
            {
                _renderTech.GetPassByIndex(p).Apply();
                _dxDevice.Draw(_vertexCount, 0);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~DxKinectPointsCloudRenderer()
        {
            Dispose(false);
        }

        protected void Dispose(bool disposing)
        {
            if (_depthMapBuffer != null)
            {
                _depthMapBuffer.Dispose();
                _depthMapBuffer = null;
            }

            if (_depthMapBufferRV != null)
            {
                _depthMapBufferRV.Dispose();
                _depthMapBufferRV = null;
            }

            if (_imageTexture != null)
            {
                _imageTexture.Dispose();
                _imageTexture = null;
            }

            if (_imageTextureRV != null)
            {
                _imageTextureRV.Dispose();
                _imageTextureRV = null;
            }

            if (_effect != null)
            {
                _effect.Dispose();
                _effect = null;
            }

            if (_inputLayout != null)
            {
                _inputLayout.Dispose();
                _inputLayout = null;
            }

            if (_vertexBuffer != null)
            {
                _vertexBuffer.Dispose();
                _vertexBuffer = null;
            }
        }
    }
}