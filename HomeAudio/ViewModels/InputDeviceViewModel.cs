using CommunityToolkit.Mvvm.ComponentModel;
using HomeAudio.Models;

namespace HomeAudio.ViewModels;

public partial class InputDeviceViewModel : ObservableObject
{
    public InputDevice Model { get; }

    public string Name      => Model.Name;
    public bool   IsDefault => Model.IsDefault;

    public InputDeviceViewModel(InputDevice model) => Model = model;

    public override string ToString() => Name;
}
