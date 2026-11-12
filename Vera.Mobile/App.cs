namespace Vera.Mobile;

public partial class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState) => new Window(new ContentPage());
}
