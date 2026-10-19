global using Microsoft.UI.Xaml.Data;
global using Microsoft.UI.Xaml.Navigation;

global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Logging;

// The interface, folder by folder: the shell, the artwork band it holds, the main screen and its
// three regions, the player, and the screens that open over them. These are global usings, not
// per-file usings, because each of these namespaces is used by the others. A screen that shows a
// chip from the deck or a splitter from Common is normal, so it does not need its own using.
global using RPM.Desktop.Presentation.Common;
global using RPM.Desktop.Presentation.Controls;
global using RPM.Desktop.Presentation.Shell;
global using RPM.Desktop.Presentation.NowPlaying;
global using RPM.Desktop.Presentation.Main;
global using RPM.Desktop.Presentation.Main.LeftPane;
global using RPM.Desktop.Presentation.Main.RightPane;
global using RPM.Desktop.Presentation.Main.Library;
global using RPM.Desktop.Presentation.Main.Transport;
global using RPM.Desktop.Presentation.RPaMp;
global using RPM.Desktop.Presentation.Dialogs;
global using RPM.Desktop.Presentation.Settings;
global using RPM.Desktop.Presentation.Setup;
global using RPM.Desktop.Presentation.Pegasus;
global using RPM.Desktop.Presentation.Viz;
global using RPM.Desktop.Presentation.Gallery;

// The controls that only one screen uses. Each is in a Controls folder under the view that owns it.
// The shared controls are in Presentation.Controls above, with the rest of the interface.
global using RPM.Desktop.Presentation.Main.Controls;
global using RPM.Desktop.Presentation.Main.LeftPane.Controls;
global using RPM.Desktop.Presentation.Main.Transport.Controls;
global using RPM.Desktop.Presentation.Dialogs.Controls;
global using RPM.Desktop.Presentation.Viz.Controls;

// The models that each of those screens binds to. Each is in a Models folder under the view that
// owns it. A view can use its own models without a using. These are listed here so that a
// neighbouring view can bind to them too.
global using RPM.Desktop.Presentation.Shell.Models;
global using RPM.Desktop.Presentation.Main.Models;
global using RPM.Desktop.Presentation.Main.LeftPane.Models;
global using RPM.Desktop.Presentation.Main.Library.Models;
global using RPM.Desktop.Presentation.RPaMp.Models;
global using RPM.Desktop.Presentation.Dialogs.Models;

// Services, grouped by purpose: audio, sending audio to a speaker, artwork, record metadata, the
// local library, the cloud copy of the library, the account, the app's own shared helpers, and the
// platform code that depends on the operating system.
global using RPM.Desktop.Services;
global using RPM.Desktop.Services.Account;
global using RPM.Desktop.Services.Art;
global using RPM.Desktop.Services.Audio;
global using RPM.Desktop.Services.Cast;
global using RPM.Desktop.Services.Catalog;
global using RPM.Desktop.Services.Cloud;
global using RPM.Desktop.Services.Library;
global using RPM.Desktop.Services.Lyrics;
global using RPM.Desktop.Services.Platform;
global using RPM.Desktop.Services.Shell;
global using RPM.Onnx;
global using RPM.Fingerprint.Net;

global using RPM.Desktop.Strings;
global using Uno.Extensions.Navigation;
global using ApplicationExecutionState = Windows.ApplicationModel.Activation.ApplicationExecutionState;
[assembly: Uno.Extensions.Reactive.Config.BindableGenerationTool(3)]
