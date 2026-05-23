using Godot;

namespace RiichiMahjong.UI
{
    public partial class IntroScreen : Control
    {
        private const float AutoAdvanceSeconds = 5f;

        private float _elapsed = 0f;
        private bool  _done    = false;

        public override void _Ready()
        {
            var bg = new ColorRect();
            bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            bg.Color = Colors.Black;
            AddChild(bg);

            var splash = GD.Load<Texture2D>("res://Assets/splash.png");
            if (splash != null)
            {
                var img = new TextureRect();
                img.Texture     = splash;
                img.ExpandMode  = TextureRect.ExpandModeEnum.IgnoreSize;
                img.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
                img.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
                AddChild(img);
            }

            var hint = new Label { Text = "Click anywhere to continue" };
            hint.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
            hint.OffsetTop    = -48f;
            hint.OffsetBottom = 0f;
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.VerticalAlignment   = VerticalAlignment.Center;
            hint.AddThemeFontSizeOverride("font_size", 18);
            hint.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.65f));
            AddChild(hint);
        }

        public override void _Process(double delta)
        {
            if (_done) return;
            _elapsed += (float)delta;
            if (_elapsed >= AutoAdvanceSeconds)
                Advance();
        }

        public override void _Input(InputEvent @event)
        {
            if (_done) return;
            bool clicked = @event is InputEventMouseButton mb && mb.Pressed;
            bool keyed   = @event is InputEventKey key && key.Pressed && !key.Echo;
            if (clicked || keyed)
                Advance();
        }

        private void Advance()
        {
            _done = true;
            GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn");
        }
    }
}
