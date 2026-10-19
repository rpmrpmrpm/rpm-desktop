// The base class that the GL stages derive from. On the WinUI head it is rpm's WinGlCanvas. That class
// reads frames back without waiting for the GPU, and it pauses while the window is minimised. On the
// Skia head it is Uno's own GLCanvasElement. That depends on Uno internals and cannot be copied (see
// WinGlCanvas).
#if RPM_WINUI
global using GlCanvas = RPM.Desktop.Presentation.Common.WinGlCanvas;
#else
global using GlCanvas = Uno.WinUI.Graphics3DGL.GLCanvasElement;
#endif
