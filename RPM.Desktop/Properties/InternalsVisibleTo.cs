using System.Runtime.CompilerServices;

// The app's own tests. Rules that decide what a window may offer (which fields a mass edit shows,
// which entries a menu carries) are internal, because nothing outside the app should call them.
// They are the part most worth testing. See RPM.Desktop.Tests. It is an Uno.Sdk project on
// net9.0-desktop, because that is the only project shape that can reference this one.
[assembly: InternalsVisibleTo("RPM.Desktop.Tests")]
