using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace PrivacyIsland.UI;

/// <summary>
/// 封装胶囊的四组动画：入场（Show）/ 呼吸（Breath）/ 脉冲（Pulse）/ 退场（Hide）。
/// <para>
/// • 所有动画只作用于 RenderTransform / Opacity / Effect，不触发布局；
/// • 全部使用 FillBehavior.Stop（配合"基线 = 终态"策略）：
///   每段动画开始前，把局部值设置为该动画的终态，动画用 From 强制起点；
///   动画结束（或中途被取消）后属性自然回落，不闪烁、不锁死属性。
/// </para>
/// </summary>
public sealed class AnimationHelper
{
    private readonly FrameworkElement _host;
    private readonly Border _capsule;
    private readonly ScaleTransform _capsuleScale;
    private readonly TranslateTransform _capsuleTranslate;
    private readonly ScaleTransform _pupilScale;
    private readonly DropShadowEffect _glow;

    private readonly Storyboard _show;
    private readonly Storyboard _hide;
    private readonly Storyboard _breath;
    private readonly Storyboard _pulse;

    /// <summary>入场动画播放完毕。</summary>
    public event Action? ShowCompleted;

    /// <summary>退场动画播放完毕。</summary>
    public event Action? HideCompleted;

    public AnimationHelper(
        FrameworkElement host,
        Border capsule,
        ScaleTransform capsuleScale,
        TranslateTransform capsuleTranslate,
        ScaleTransform pupilScale,
        DropShadowEffect glow)
    {
        _host = host;
        _capsule = capsule;
        _capsuleScale = capsuleScale;
        _capsuleTranslate = capsuleTranslate;
        _pupilScale = pupilScale;
        _glow = glow;

        _show = BuildShow();
        _hide = BuildHide();
        _breath = BuildBreath();
        _pulse = BuildPulse();

        _show.Completed += (_, _) => ShowCompleted?.Invoke();
        _hide.Completed += (_, _) => HideCompleted?.Invoke();
    }

    // =====================================================================
    // 对外播放接口
    // =====================================================================

    /// <summary>播放入场动画（Y -60→0 / 缩放 0.9→1 / 透明度 0→1）。</summary>
    public void PlayShow()
    {
        RemoveAll();
        SetVisibleState(); // 基线 = 终态
        _show.Begin(_host, isControllable: true);
    }

    /// <summary>播放退场动画（Y 0→-60 / 缩放 1→0.9 / 透明度 1→0）。</summary>
    public void PlayHide()
    {
        RemoveAll();
        SetHiddenState(); // 基线 = 终态（隐藏态），中途取消也不会闪到可见位置
        _hide.Begin(_host, isControllable: true);
    }

    /// <summary>启动持续呼吸动画（瞳孔缩放 / 整体透明度 / 光晕半径，循环往返）。</summary>
    public void StartBreath()
    {
        SafeRemove(_breath);
        _breath.Begin(_host, isControllable: true);
    }

    /// <summary>停止呼吸动画。</summary>
    public void StopBreath() => SafeRemove(_breath);

    /// <summary>播放一次脉冲（1 → 1.12 → 1），不改变显示 / 隐藏状态。</summary>
    public void PlayPulse()
    {
        SafeRemove(_pulse);
        _pulse.Begin(_host, isControllable: true);
    }

    /// <summary>停止全部动画（用于取消 Hide 等场景）。</summary>
    public void StopAll() => RemoveAll();

    /// <summary>把几何属性设置为"完全显示"状态。</summary>
    public void SetVisibleState()
    {
        _capsuleTranslate.Y = 0;
        _capsuleScale.ScaleX = 1;
        _capsuleScale.ScaleY = 1;
        _capsule.Opacity = 1;
    }

    /// <summary>把几何属性设置为"完全隐藏"状态（滑出 + 缩小 + 透明）。</summary>
    public void SetHiddenState()
    {
        _capsuleTranslate.Y = -60;
        _capsuleScale.ScaleX = 0.9;
        _capsuleScale.ScaleY = 0.9;
        _capsule.Opacity = 0;
    }

    // =====================================================================
    // Storyboard 构建
    // =====================================================================

    private Storyboard BuildShow()
    {
        var sb = new Storyboard { FillBehavior = FillBehavior.Stop };

        // 期望帧率上限 60（高刷屏上省电；DWM 硬件加速场景下由系统自行调度）
        Timeline.SetDesiredFrameRate(sb, 60);

        // 滑入：Y -60 → 0，0.32s，CubicEase.EaseOut
        Add(sb, _capsuleTranslate, TranslateTransform.YProperty, new DoubleAnimation
        {
            From = -60,
            To = 0,
            Duration = TimeSpan.FromSeconds(0.32),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        });

        // 缩放：0.9 → 1，0.28s，CubicEase.EaseOut
        Add(sb, _capsuleScale, ScaleTransform.ScaleXProperty, new DoubleAnimation
        {
            From = 0.9,
            To = 1,
            Duration = TimeSpan.FromSeconds(0.28),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        });

        Add(sb, _capsuleScale, ScaleTransform.ScaleYProperty, new DoubleAnimation
        {
            From = 0.9,
            To = 1,
            Duration = TimeSpan.FromSeconds(0.28),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        });

        // 透明度：0 → 1，0.18s，Linear
        Add(sb, _capsule, UIElement.OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromSeconds(0.18),
            FillBehavior = FillBehavior.Stop,
        });

        return sb;
    }

    private Storyboard BuildHide()
    {
        var sb = new Storyboard { FillBehavior = FillBehavior.Stop };

        // 期望帧率上限 60（高刷屏上省电；DWM 硬件加速场景下由系统自行调度）
        Timeline.SetDesiredFrameRate(sb, 60);

        // 滑出：Y 0 → -60，0.26s，CubicEase.EaseIn
        Add(sb, _capsuleTranslate, TranslateTransform.YProperty, new DoubleAnimation
        {
            From = 0,
            To = -60,
            Duration = TimeSpan.FromSeconds(0.26),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop,
        });

        // 缩放：1 → 0.9，0.22s，CubicEase.EaseIn
        Add(sb, _capsuleScale, ScaleTransform.ScaleXProperty, new DoubleAnimation
        {
            From = 1,
            To = 0.9,
            Duration = TimeSpan.FromSeconds(0.22),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop,
        });

        Add(sb, _capsuleScale, ScaleTransform.ScaleYProperty, new DoubleAnimation
        {
            From = 1,
            To = 0.9,
            Duration = TimeSpan.FromSeconds(0.22),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop,
        });

        // 透明度：1 → 0，0.2s，Linear
        Add(sb, _capsule, UIElement.OpacityProperty, new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromSeconds(0.2),
            FillBehavior = FillBehavior.Stop,
        });

        return sb;
    }

    private Storyboard BuildBreath()
    {
        var sb = new Storyboard { FillBehavior = FillBehavior.Stop };

        // 期望帧率上限 60（高刷屏上省电；DWM 硬件加速场景下由系统自行调度）
        Timeline.SetDesiredFrameRate(sb, 60);

        // 瞳孔缩放：1 → 1.08，0.9s，SineEase.EaseInOut
        Add(sb, _pupilScale, ScaleTransform.ScaleXProperty, BreathAnimation(1.0, 1.08, 0.9));
        Add(sb, _pupilScale, ScaleTransform.ScaleYProperty, BreathAnimation(1.0, 1.08, 0.9));

        // 整体透明度：0.85 → 1，1.2s
        Add(sb, _capsule, UIElement.OpacityProperty, BreathAnimation(0.85, 1.0, 1.2));

        // 光晕半径：8 → 14，1.1s
        Add(sb, _glow, DropShadowEffect.BlurRadiusProperty, BreathAnimation(8, 14, 1.1));

        return sb;
    }

    private Storyboard BuildPulse()
    {
        var sb = new Storyboard { FillBehavior = FillBehavior.Stop };

        // 期望帧率上限 60（高刷屏上省电；DWM 硬件加速场景下由系统自行调度）
        Timeline.SetDesiredFrameRate(sb, 60);

        // 1 → 1.12 → 1，总时长 0.3s
        Add(sb, _capsuleScale, ScaleTransform.ScaleXProperty, PulseAnimation());
        Add(sb, _capsuleScale, ScaleTransform.ScaleYProperty, PulseAnimation());

        return sb;
    }

    // =====================================================================
    // 辅助
    // =====================================================================

    private static DoubleAnimation BreathAnimation(double from, double to, double seconds) => new()
    {
        From = from,
        To = to,
        Duration = TimeSpan.FromSeconds(seconds),
        AutoReverse = true,
        RepeatBehavior = RepeatBehavior.Forever,
        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        FillBehavior = FillBehavior.Stop,
    };

    private static DoubleAnimationUsingKeyFrames PulseAnimation()
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(0.3),
            FillBehavior = FillBehavior.Stop,
        };

        animation.KeyFrames.Add(new EasingDoubleKeyFrame(
            1.0,
            KeyTime.FromTimeSpan(TimeSpan.Zero)));

        animation.KeyFrames.Add(new EasingDoubleKeyFrame(
            1.12,
            KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.15)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

        animation.KeyFrames.Add(new EasingDoubleKeyFrame(
            1.0,
            KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.30)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        });

        return animation;
    }

    private void RemoveAll()
    {
        SafeRemove(_show);
        SafeRemove(_hide);
        SafeRemove(_breath);
        SafeRemove(_pulse);
    }

    private void SafeRemove(Storyboard storyboard)
    {
        try { storyboard.Remove(_host); } catch { }
    }

    private static void Add(Storyboard storyboard, DependencyObject target, DependencyProperty property, Timeline animation)
    {
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        storyboard.Children.Add(animation);
    }
}
