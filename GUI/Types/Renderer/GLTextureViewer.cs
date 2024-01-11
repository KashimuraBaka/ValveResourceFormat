using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using GUI.Controls;
using GUI.Utils;
using OpenTK.Graphics.OpenGL;
using SkiaSharp;
using ValveResourceFormat;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.TextureDecoders;
using ValveResourceFormat.Utils;

namespace GUI.Types.Renderer
{
    class GLTextureViewer : GLViewerControl, IGLViewer
    {
        private VrfGuiContext GuiContext;
        private Resource Resource;
        private SKBitmap Bitmap;
        private RenderTexture texture;
        private Shader shader;
        private int vao;

        private Vector2? ClickPosition;
        private Vector2 Position;
        private Vector2 PositionOld;
        private float TextureScale = 1f;
        private float TextureScaleOld = 1f;
        private float TextureScaleChangeTime = 10f;

        private int SelectedMip;
        private int SelectedDepth;
        private ChannelMapping SelectedChannels = ChannelMapping.RGB;
        private bool WantsSeparateAlpha;
        private TextureCodec decodeFlags;

        private bool FirstPaint = true;
        private CheckedListBox decodeFlagsListBox;

        private Vector2 ActualTextureSize
        {
            get
            {
                if (WantsSeparateAlpha)
                {
                    var isWide = texture.Width > texture.Height;
                    return new Vector2(
                        isWide ? texture.Width : texture.Width * 2,
                        isWide ? texture.Height * 2 : texture.Height
                    );
                }

                return new Vector2(texture.Width, texture.Height);
            }
        }

        private Vector2 ActualTextureSizeScaled => ActualTextureSize * TextureScale;
        private bool IsZoomedIn;
        private bool MovedFromOrigin_Unzoomed;

        const int DefaultSelection = 3;
        static readonly (ChannelMapping Channels, bool SplitAlpha, string ChoiceString)[] ChannelsComboBoxOrder = [
            (ChannelMapping.R, false, "Red"),
            (ChannelMapping.G, false, "Green"),
            (ChannelMapping.B, false, "Blue"),
            (ChannelMapping.RGB, false, "Opaque"),
            (ChannelMapping.RGBA, false, "Transparent"),
            (ChannelMapping.A, false, "Alpha"),
            (ChannelMapping.RGBA, true, "Opaque with split Alpha"),
        ];

        private GLTextureViewer(VrfGuiContext guiContext) : base()
        {
            GuiContext = guiContext;

            GLLoad += OnLoad;
            GLControl.MouseMove += OnMouseMove;

            SetZoomLabel();

            var resetButton = new Button
            {
                Text = "Reset zoom",
                AutoSize = true,
            };

            resetButton.Click += (_, __) =>
            {
                TextureScaleOld = TextureScale;
                TextureScale = 1f;
                TextureScaleChangeTime = 0f;

                PositionOld = Position;
                CenterPosition();

                SetZoomLabel();
            };

            AddControl(resetButton);
        }

        public GLTextureViewer(VrfGuiContext guiContext, SKBitmap bitmap) : this(guiContext)
        {
            Bitmap = bitmap;
        }

        public GLTextureViewer(VrfGuiContext guiContext, Resource resource) : this(guiContext)
        {
            Resource = resource;

            var textureData = (Texture)Resource.DataBlock;

            AddControl(new Label
            {
                Text = $"Size: {textureData.Width}x{textureData.Height}",
                Width = 200,
            });
            AddControl(new Label
            {
                Text = $"Format: {textureData.Format}",
                Width = 200,
            });

            ComboBox mipComboBox = null;

            if (textureData.NumMipLevels > 1)
            {
                mipComboBox = AddSelection("Mip level", (name, index) =>
                {
                    SelectedMip = index;
                });

                mipComboBox.Items.AddRange(Enumerable.Range(0, textureData.NumMipLevels).Select(x => $"#{x}").ToArray());
                mipComboBox.SelectedIndex = 0;
            }

            if (textureData.Depth > 1)
            {
                var depthComboBox = AddSelection("Depth", (name, index) =>
                {
                    SelectedDepth = index;
                });

                depthComboBox.Items.AddRange(Enumerable.Range(0, textureData.Depth).Select(x => $"#{x}").ToArray());
                depthComboBox.SelectedIndex = 0;
            }

            decodeFlagsListBox = AddMultiSelection("Texture Conversion",
                SetInitialDecodeFlagsState,
                checkedItemNames =>
                {
                    decodeFlags = TextureCodec.None;

                    foreach (var itemName in checkedItemNames)
                    {
                        decodeFlags |= (TextureCodec)Enum.Parse(typeof(TextureCodec), itemName);
                    }
                }
            );

            var channelsComboBox = AddSelection("Channels", (name, index) =>
            {
                if (texture == null)
                {
                    return;
                }

                var wasSeparateAlpha = WantsSeparateAlpha;
                var oldTextureSize = ActualTextureSizeScaled;

                SelectedChannels = ChannelsComboBoxOrder[index].Channels;
                WantsSeparateAlpha = ChannelsComboBoxOrder[index].SplitAlpha;

                if (wasSeparateAlpha || WantsSeparateAlpha)
                {
                    TextureScaleChangeTime = 0f;
                    TextureScaleOld = TextureScale;

                    PositionOld = Position;
                    Position -= oldTextureSize / 2f;
                    Position += ActualTextureSizeScaled / 2f;

                    ClampPosition();
                }
            });

            for (var i = 0; i < ChannelsComboBoxOrder.Length; i++)
            {
                channelsComboBox.Items.Add(ChannelsComboBoxOrder[i].ChoiceString);
            }

            channelsComboBox.SelectedIndex = DefaultSelection;

            var forceSoftwareDecode = textureData.IsRawJpeg || textureData.IsRawPng;
            var softwareDecodeCheckBox = AddCheckBox("Software decode", forceSoftwareDecode, (state) =>
            {
                SetupTexture(state);

                if (mipComboBox != null)
                {
                    mipComboBox.Enabled = !state;
                }
            });

            if (forceSoftwareDecode)
            {
                softwareDecodeCheckBox.Enabled = false;
            }
        }

        private void SetInitialDecodeFlagsState(CheckedListBox listBox)
        {
            listBox.Items.Clear();
            var values = Enum.GetValues(typeof(TextureCodec));

            var i = 0;
            for (var flag = 0; flag < values.Length; flag++)
            {
                var value = (TextureCodec)values.GetValue(flag);
                var name = Enum.GetName(value);

                // check for combined flag, or flag 0 (none)
                if (value == 0 || (value & (value - 1)) != 0)
                {
                    continue;
                }

                listBox.Items.Add(name);
                var setCheckedState = decodeFlags.HasFlag(value);
                listBox.SetItemChecked(i, setCheckedState);
                i++;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                GLControl.MouseMove -= OnMouseMove;
                GLPaint -= OnPaint;

                GuiContext = null;
                Resource = null;

                Bitmap?.Dispose();
                Bitmap = null;

                decodeFlagsListBox?.Dispose();
                decodeFlagsListBox = null;

                texture?.Dispose();
            }

            base.Dispose(disposing);
        }

        private void SetZoomLabel() => SetMoveSpeedOrZoomLabel($"Zoom: {TextureScale * 100:0.0}% (scroll to change)");

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (ClickPosition == null)
            {
                return;
            }

            var oldPosition = Position;
            var mousePosition = new Vector2(e.Location.X, e.Location.Y);

            Position = ClickPosition.Value - mousePosition;

            ClampPosition();

            // When cursor moves past the edge, but the picture does not move, update click position
            // so that moving mouse in opposite direction instantly moves the picture, instead of waiting to move to the initial click position
            if (oldPosition == Position)
            {
                ClickPosition = Position + mousePosition;
            }
        }

        protected override void OnMouseDown(object sender, MouseEventArgs e)
        {
            ClickPosition = Position + new Vector2(e.Location.X, e.Location.Y);
        }

        protected override void OnMouseUp(object sender, MouseEventArgs mouseEventArgs)
        {
            ClickPosition = null;
        }

        protected override void OnMouseWheel(object sender, MouseEventArgs e)
        {
            (TextureScaleOld, PositionOld) = GetCurrentPositionAndScale();
            TextureScaleChangeTime = 0f;
            ClickPosition = null;

            if (e.Delta < 0)
            {
                TextureScale /= 1.25f;
            }
            else
            {
                TextureScale *= 1.25f;
            }

            var scaleMinMax = new Vector2(0.1f, 50f);
            scaleMinMax *= 256 / MathF.Max(ActualTextureSize.X, ActualTextureSize.Y);

            TextureScale = Math.Clamp(TextureScale, scaleMinMax.X, scaleMinMax.Y);

            var pos = new Vector2(e.Location.X, e.Location.Y);
            var posPrev = (pos + PositionOld) / TextureScaleOld;
            var posNewScale = posPrev * TextureScale;
            Position = posNewScale - pos;

            ClampPosition();
            SetZoomLabel();
        }

        private void ClampPosition()
        {
            var width = ActualTextureSizeScaled.X;
            var height = ActualTextureSizeScaled.Y;

            if (ClickPosition != null && !IsZoomedIn)
            {
                MovedFromOrigin_Unzoomed = true;
            }

            IsZoomedIn = GLControl.Height < height && GLControl.Width < width;

            if (IsZoomedIn)
            {
                Position.X = Math.Clamp(Position.X, 0, width - GLControl.Width);
                Position.Y = Math.Clamp(Position.Y, 0, height - GLControl.Height);
                MovedFromOrigin_Unzoomed = false;
                return;
            }

            if (MovedFromOrigin_Unzoomed)
            {
                Position.X = Math.Clamp(Position.X, Math.Min(0, -GLControl.Width + width), 0);
                Position.Y = Math.Clamp(Position.Y, Math.Min(0, -GLControl.Height + height), 0);
            }
            else
            {
                CenterPosition();
            }
        }

        private void CenterPosition()
        {
            Position = -new Vector2(
                GLControl.Width / 2f - ActualTextureSizeScaled.X / 2f,
                GLControl.Height / 2f - ActualTextureSizeScaled.Y / 2f
            );
        }

        protected override void OnResize(object sender, EventArgs e)
        {
            base.OnResize(sender, e);

            if (texture != null)
            {
                ClampPosition();
            }
        }

        private void SetupTexture(bool forceSoftwareDecode)
        {
            texture?.Dispose();

            UploadTexture(forceSoftwareDecode);

            if (decodeFlagsListBox != null)
            {
                SetInitialDecodeFlagsState(decodeFlagsListBox);
            }

            using (texture.BindingContext())
            {
                texture.SetWrapMode(TextureWrapMode.ClampToEdge);
                texture.SetFiltering(TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Nearest);
            }

            var textureType = "TYPE_" + texture.Target.ToString().ToUpperInvariant();

            var arguments = new Dictionary<string, byte>
            {
                [textureType] = 1,
            };

            shader = GuiContext.ShaderLoader.LoadShader("vrf.texture_decode", arguments);
        }

        private void UploadTexture(bool forceSoftwareDecode)
        {
            if (Resource == null)
            {
                Debug.Assert(Bitmap != null);
                Debug.Assert(Bitmap.ColorType == SKColorType.Bgra8888);

                texture = new RenderTexture(TextureTarget.Texture2D, Bitmap.Width, Bitmap.Height, 1, 1);
                decodeFlags = TextureCodec.None;

                using var _ = texture.BindingContext();
                GL.TexImage2D(texture.Target, 0, PixelInternalFormat.Rgba8, texture.Width, texture.Height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, Bitmap.GetPixels());
                GL.TexParameter(texture.Target, TextureParameterName.TextureMaxLevel, 0);

                return;
            }

            var textureData = (Texture)Resource.DataBlock;
            var isCpuDecodedFormat = textureData.IsRawJpeg || textureData.IsRawPng;

            if (isCpuDecodedFormat || forceSoftwareDecode)
            {
                SKBitmap bitmap;

                // GUI provides hardware decoder for texture decoding, but here we do not want to use it
                var decoder = HardwareAcceleratedTextureDecoder.Decoder;
                HardwareAcceleratedTextureDecoder.Decoder = null;

                try
                {
                    bitmap = textureData.GenerateBitmap();
                }
                finally
                {
                    HardwareAcceleratedTextureDecoder.Decoder = decoder;
                }

                using (bitmap)
                {
                    Debug.Assert(bitmap.ColorType == SKColorType.Bgra8888);

                    texture = new RenderTexture(TextureTarget.Texture2D, textureData);
                    decodeFlags = TextureCodec.None;

                    using var _ = texture.BindingContext();
                    GL.TexImage2D(texture.Target, 0, PixelInternalFormat.Rgba8, texture.Width, texture.Height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, bitmap.GetPixels());
                    GL.TexParameter(texture.Target, TextureParameterName.TextureMaxLevel, 0);
                }

                return;
            }

            // TODO: LoadTexture has things like max texture size and anisotrophy, need to ignore these
            texture = GuiContext.MaterialLoader.LoadTexture(Resource, isViewerRequest: true);
            decodeFlags = textureData.RetrieveCodecFromResourceEditInfo();
        }

        private void OnLoad(object sender, EventArgs e)
        {
            SetupTexture(false);

            vao = GL.GenVertexArray();

            MainFramebuffer.ClearColor = OpenTK.Graphics.Color4.Green;
            MainFramebuffer.ClearMask = ClearBufferMask.ColorBufferBit;
            GL.DepthMask(false);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);

            GLLoad -= OnLoad;
            GLPaint += OnPaint;

#if DEBUG
            // TODO: Remove this later
            void Hotload(object s, System.IO.FileSystemEventArgs e)
            {
                if (e.FullPath.EndsWith(".TMP", StringComparison.Ordinal))
                {
                    return;
                }

                GuiContext.ShaderLoader.ClearCache();

                shader = GuiContext.ShaderLoader.LoadShader("vrf.texture_decode", shader.Parameters);
            }

            GuiContext.ShaderLoader.ShaderWatcher.SynchronizingObject = this;
            GuiContext.ShaderLoader.ShaderWatcher.Changed += Hotload;
            GuiContext.ShaderLoader.ShaderWatcher.Created += Hotload;
            GuiContext.ShaderLoader.ShaderWatcher.Renamed += Hotload;
#endif
        }

        private void OnPaint(object sender, RenderEventArgs e)
        {
            if (FirstPaint)
            {
                FirstPaint = false; // OnLoad has control size of 0 for some reason

                if (GLControl.Width < ActualTextureSize.X || GLControl.Height < ActualTextureSize.Y)
                {
                    // Initially scale image to fit if it's bigger than the viewport
                    TextureScale = Math.Min(
                        GLControl.Width / ActualTextureSize.X,
                        GLControl.Height / ActualTextureSize.Y
                    );
                }
                else
                {
                    // Initially scale image to the minimum scale if it's very small
                    TextureScale = Math.Max(
                        1f,
                        0.1f * 256f / MathF.Max(ActualTextureSize.X, ActualTextureSize.Y)
                    );
                }

                SetZoomLabel();

                Position = -new Vector2(
                    GLControl.Width / 2f - ActualTextureSizeScaled.X / 2f,
                    GLControl.Height / 2f - ActualTextureSizeScaled.Y / 2f
                );
            }

            TextureScaleChangeTime += e.FrameTime;

            var (scale, position) = GetCurrentPositionAndScale();

            GL.Viewport(0, 0, GLControl.Width, GLControl.Height);
            MainFramebuffer.Clear();

            GL.UseProgram(shader.Program);

            //shader.SetUniform4x4("transform", Matrix4x4.CreateOrthographic(1f, 1f, 0, 1));
            shader.SetUniform1("g_bTextureViewer", 1u);
            shader.SetUniform2("g_vViewportSize", new Vector2(MainFramebuffer.Width, MainFramebuffer.Height));
            shader.SetUniform2("g_vViewportPosition", position);
            shader.SetUniform1("g_flScale", scale);

            shader.SetTexture(0, "g_tInputTexture", texture);
            shader.SetUniform4("g_vInputTextureSize", new Vector4(texture.Width, texture.Height, texture.Depth, texture.NumMipLevels));
            shader.SetUniform1("g_nSelectedMip", SelectedMip);
            shader.SetUniform1("g_nSelectedDepth", SelectedDepth);
            shader.SetUniform1("g_nSelectedChannels", SelectedChannels.PackedValue);
            shader.SetUniform1("g_bWantsSeparateAlpha", WantsSeparateAlpha ? 1u : 0u);
            shader.SetUniform1("g_nDecodeFlags", (int)decodeFlags);

            GL.BindVertexArray(vao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }

        private (float Scale, Vector2 Position) GetCurrentPositionAndScale()
        {
            var time = Math.Min(TextureScaleChangeTime / 0.4f, 1.0f);
            time = 1f - MathF.Pow(1f - time, 5f); // easeOutQuint

            var position = Vector2.Lerp(PositionOld, Position, time);
            var scale = float.Lerp(TextureScaleOld, TextureScale, time);

            return (scale, position);
        }
    }
}