using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Xunit;
using Path = System.IO.Path;

namespace OpenPin.Tests.Packaging;

public class SocialBannerGeneratorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "OpenPin.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void GenerateSocialPreviewImages()
    {
        var repoRoot = FindRepoRoot();
        var assetsDir = Path.Combine(repoRoot, "assets");
        var logoPath = Path.Combine(assetsDir, "logo.png");
        var heroPath = Path.Combine(assetsDir, "hero_preview.png");
        var outputPath = Path.Combine(assetsDir, "social_preview.png");
        var outputPath2x = Path.Combine(assetsDir, "social_preview_2x.png");

        Assert.True(File.Exists(logoPath), "logo.png must exist");
        Assert.True(File.Exists(heroPath), "hero_preview.png must exist");

        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                RenderBanners(logoPath, heroPath, outputPath, outputPath2x);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error != null) throw new Xunit.Sdk.XunitException("Banner generation failed: " + error);

        Assert.True(File.Exists(outputPath), "social_preview.png must be created");
        Assert.True(new FileInfo(outputPath).Length > 50_000, "social_preview.png must be > 50KB");

        Assert.True(File.Exists(outputPath2x), "social_preview_2x.png must be created");
        Assert.True(new FileInfo(outputPath2x).Length > 100_000, "social_preview_2x.png must be > 100KB");
    }

    private static void RenderBanners(string logoPath, string heroPath, string outputPath, string outputPath2x)
    {
        var logoBitmap = new BitmapImage();
        logoBitmap.BeginInit();
        logoBitmap.UriSource = new Uri(logoPath, UriKind.Absolute);
        logoBitmap.CacheOption = BitmapCacheOption.OnLoad;
        logoBitmap.EndInit();
        logoBitmap.Freeze();

        var heroBitmap = new BitmapImage();
        heroBitmap.BeginInit();
        heroBitmap.UriSource = new Uri(heroPath, UriKind.Absolute);
        heroBitmap.CacheOption = BitmapCacheOption.OnLoad;
        heroBitmap.EndInit();
        heroBitmap.Freeze();

        // 1. Render 1280 x 640 standard banner
        var visual = BuildBannerVisual(logoBitmap, heroBitmap);
        visual.Measure(new Size(1280, 640));
        visual.Arrange(new Rect(0, 0, 1280, 640));
        visual.UpdateLayout();

        var rtb = new RenderTargetBitmap(1280, 640, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(outputPath))
        {
            encoder.Save(fs);
        }

        // 2. Render 2560 x 1280 2x Retina banner
        var visual2x = BuildBannerVisual(logoBitmap, heroBitmap);
        visual2x.LayoutTransform = new ScaleTransform(2.0, 2.0);
        visual2x.Measure(new Size(2560, 1280));
        visual2x.Arrange(new Rect(0, 0, 2560, 1280));
        visual2x.UpdateLayout();

        var rtb2x = new RenderTargetBitmap(2560, 1280, 96, 96, PixelFormats.Pbgra32);
        rtb2x.Render(visual2x);

        var encoder2x = new PngBitmapEncoder();
        encoder2x.Frames.Add(BitmapFrame.Create(rtb2x));
        using (var fs2x = File.Create(outputPath2x))
        {
            encoder2x.Save(fs2x);
        }
    }

    private static FrameworkElement BuildBannerVisual(BitmapSource logoBitmap, BitmapSource heroBitmap)
    {
        var root = new Grid
        {
            Width = 1280,
            Height = 640,
            Background = new SolidColorBrush(Color.FromRgb(10, 14, 23)) // #0A0E17
        };

        // Ambient glowing lights background
        var bgCanvas = new Canvas { IsHitTestVisible = false };

        // Cyan-blue glow (top-left)
        var blueGlow = new Ellipse
        {
            Width = 720,
            Height = 720,
            Fill = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x35, 0x02, 0x84, 0xC7), 0),
                    new GradientStop(Color.FromArgb(0x14, 0x03, 0x69, 0xA1), 0.5),
                    new GradientStop(Color.FromArgb(0x00, 0x0A, 0x0E, 0x17), 1)
                }
            }
        };
        Canvas.SetLeft(blueGlow, -160);
        Canvas.SetTop(blueGlow, -220);
        bgCanvas.Children.Add(blueGlow);

        // Violet-purple glow (bottom-right)
        var purpleGlow = new Ellipse
        {
            Width = 820,
            Height = 820,
            Fill = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x32, 0x7C, 0x3A, 0xED), 0),
                    new GradientStop(Color.FromArgb(0x12, 0x6D, 0x28, 0xD9), 0.55),
                    new GradientStop(Color.FromArgb(0x00, 0x0A, 0x0E, 0x17), 1)
                }
            }
        };
        Canvas.SetLeft(purpleGlow, 580);
        Canvas.SetTop(purpleGlow, 80);
        bgCanvas.Children.Add(purpleGlow);

        // Emerald glow (top-center)
        var greenGlow = new Ellipse
        {
            Width = 520,
            Height = 520,
            Fill = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x18, 0x10, 0xB9, 0x81), 0),
                    new GradientStop(Color.FromArgb(0x00, 0x0A, 0x0E, 0x17), 0.9)
                }
            }
        };
        Canvas.SetLeft(greenGlow, 340);
        Canvas.SetTop(greenGlow, -160);
        bgCanvas.Children.Add(greenGlow);

        root.Children.Add(bgCanvas);

        // Outer border
        var outerBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x3D)),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false
        };
        root.Children.Add(outerBorder);

        // Main Two-Column Content Grid
        var mainGrid = new Grid
        {
            Margin = new Thickness(56, 44, 56, 44)
        };
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(620) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Left Panel
        var leftPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        // Header: Logo + Title + Version + Slogan
        var header = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var logoBorder = new Border
        {
            Width = 62,
            Height = 62,
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x1E, 0x33)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
            BorderThickness = new Thickness(1.5),
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(0x38, 0xBD, 0xF8),
                BlurRadius = 22,
                ShadowDepth = 0,
                Opacity = 0.45
            },
            Child = new Image
            {
                Source = logoBitmap,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(8)
            }
        };
        header.Children.Add(logoBorder);

        var titleBlock = new StackPanel { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new TextBlock
        {
            Text = "OpenPin",
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI"),
            FontWeight = FontWeights.Bold,
            FontSize = 42,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        });

        var verBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x02, 0x84, 0xC7)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "v0.1.0",
                Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
                FontSize = 12,
                FontWeight = FontWeights.Bold
            }
        };
        titleRow.Children.Add(verBorder);
        titleBlock.Children.Add(titleRow);

        titleBlock.Children.Add(new TextBlock
        {
            Text = "开源免费 · 隐私优先 · 现代 Windows 截图与贴图",
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
            FontSize = 13.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xD2, 0xFF)),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 3, 0, 0)
        });
        header.Children.Add(titleBlock);
        leftPanel.Children.Add(header);

        // Badges Row
        var badgeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
        badgeRow.Children.Add(MakeBadge("⚡ Windows 10 / 11", Color.FromRgb(0x11, 0x1C, 0x30), Color.FromRgb(0x02, 0x84, 0xC7), Color.FromRgb(0x7D, 0xD3, 0xFC)));
        badgeRow.Children.Add(MakeBadge("🎯 .NET 10 LTS · 原生 WPF", Color.FromRgb(0x1B, 0x16, 0x33), Color.FromRgb(0x7C, 0x3A, 0xED), Color.FromRgb(0xC4, 0xB5, 0xFD), 10));
        badgeRow.Children.Add(MakeBadge("🛡️ 100% 离线纯净", Color.FromRgb(0x0E, 0x23, 0x1C), Color.FromRgb(0x05, 0x96, 0x69), Color.FromRgb(0x6E, 0xE7, 0xB7), 10));
        leftPanel.Children.Add(badgeRow);

        // Catchphrase Slogan
        leftPanel.Children.Add(new TextBlock
        {
            Text = "像素级智能选区，灵动桌面自由贴图",
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI"),
            FontSize = 24.5,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)),
            Margin = new Thickness(0, 20, 0, 0)
        });
        leftPanel.Children.Add(new TextBlock
        {
            Text = "Next-Gen Lightweight Screenshot, Desktop Pin & Productivity Tool",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            Margin = new Thickness(0, 4, 0, 0)
        });

        // Feature Cards Grid
        var featureGrid = new Grid { Margin = new Thickness(0, 18, 16, 0) };
        featureGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        featureGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        featureGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        featureGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        featureGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var c1 = MakeFeatureCard("🎯", "智能选区 & 放大镜", "窗口智能感知 · RGB/HEX 一键取色", new Thickness(0, 0, 8, 8));
        Grid.SetRow(c1, 0); Grid.SetColumn(c1, 0); featureGrid.Children.Add(c1);

        var c2 = MakeFeatureCard("📌", "自由贴图 & 分组管理", "滚轮调透明度 · 缩放旋转 · 鼠标穿透", new Thickness(0, 0, 0, 8));
        Grid.SetRow(c2, 0); Grid.SetColumn(c2, 1); featureGrid.Children.Add(c2);

        var c3 = MakeFeatureCard("🔍", "离线 OCR & AI 翻译", "本地离线文字提取 · DeepSeek 大模型", new Thickness(0, 0, 8, 8));
        Grid.SetRow(c3, 1); Grid.SetColumn(c3, 0); featureGrid.Children.Add(c3);

        var c4 = MakeFeatureCard("📐", "LaTeX 数学公式识别", "公式贴图卡片 · 实时编辑预览与导出", new Thickness(0, 0, 0, 8));
        Grid.SetRow(c4, 1); Grid.SetColumn(c4, 1); featureGrid.Children.Add(c4);

        var c5 = MakeFeatureCardWide("🎞️", "长截图滚动自动拼接 · 高清录屏（MP4 / GIF 动图）", "全套专业矢量标注 · 平滑马赛克脱敏 · 免安装自包含开箱即用");
        Grid.SetRow(c5, 2); Grid.SetColumnSpan(c5, 2); featureGrid.Children.Add(c5);

        leftPanel.Children.Add(featureGrid);

        // Bottom Footer Bar
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 22, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var ghBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x02, 0x84, 0xC7)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 8, 0),
            Child = new TextBlock
            {
                Text = "GitHub",
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeights.Bold
            }
        };
        footer.Children.Add(ghBadge);

        footer.Children.Add(new TextBlock
        {
            Text = "github.com/biubiu223/OpenPin",
            Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xC5, 0xFD)),
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        footer.Children.Add(new TextBlock { Text = "·", Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)), FontSize = 14, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        footer.Children.Add(new TextBlock { Text = "MIT License", Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        footer.Children.Add(new TextBlock { Text = "·", Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)), FontSize = 14, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        footer.Children.Add(new TextBlock { Text = "永久开源免费", Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)), FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        leftPanel.Children.Add(footer);

        Grid.SetColumn(leftPanel, 0);
        mainGrid.Children.Add(leftPanel);

        // Right Panel: Elevated Window Showcase with Hero Image
        var rightContainer = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };

        var rightGlow = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromRgb(0x02, 0x84, 0xC7)),
            Opacity = 0.18,
            Margin = new Thickness(-8),
            Effect = new BlurEffect { Radius = 32 }
        };
        rightContainer.Children.Add(rightGlow);

        var windowFrame = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x38, 0x54)),
            BorderThickness = new Thickness(1.5),
            Background = new SolidColorBrush(Color.FromRgb(0x13, 0x1B, 0x2C)),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 36,
                ShadowDepth = 12,
                Opacity = 0.75,
                Direction = 315
            }
        };

        var windowGrid = new Grid();

        // Titlebar
        var titlebar = new Grid { Height = 28, VerticalAlignment = VerticalAlignment.Top, Background = new SolidColorBrush(Color.FromRgb(0x18, 0x22, 0x36)) };
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        dots.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), Margin = new Thickness(0, 0, 6, 0) });
        dots.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), Margin = new Thickness(0, 0, 6, 0) });
        dots.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)) });
        titlebar.Children.Add(dots);

        titlebar.Children.Add(new TextBlock
        {
            Text = "OpenPin - Fluent Capture & Pin Preview",
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        windowGrid.Children.Add(titlebar);

        // Preview Image Content
        var imageBorder = new Border
        {
            Margin = new Thickness(0, 28, 0, 0),
            CornerRadius = new CornerRadius(0, 0, 12, 12),
            ClipToBounds = true,
            Child = new Image
            {
                Source = heroBitmap,
                Stretch = Stretch.UniformToFill,
                Height = 360
            }
        };
        windowGrid.Children.Add(imageBorder);

        // Floating Pill Top-Right
        var floatPill1 = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 38, 12, 0),
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x0F, 0x17, 0x2A)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 5, 10, 5),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 4, Opacity = 0.55 },
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "✨", Margin = new Thickness(0, 0, 4, 0) },
                    new TextBlock { Text = "Windows 11 Fluent UI", Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)), FontSize = 11, FontWeight = FontWeights.Bold }
                }
            }
        };
        windowGrid.Children.Add(floatPill1);

        // Floating Pill Bottom-Left
        var floatPill2 = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12, 0, 0, 12),
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x0F, 0x17, 0x2A)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 5, 10, 5),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 4, Opacity = 0.55 },
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "📦", Margin = new Thickness(0, 0, 4, 0) },
                    new TextBlock { Text = "免安装绿色版 / 集成安装包", Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)), FontSize = 11, FontWeight = FontWeights.Bold }
                }
            }
        };
        windowGrid.Children.Add(floatPill2);

        windowFrame.Child = windowGrid;
        rightContainer.Children.Add(windowFrame);

        Grid.SetColumn(rightContainer, 1);
        mainGrid.Children.Add(rightContainer);

        root.Children.Add(mainGrid);
        return root;
    }

    private static Border MakeBadge(string text, Color bg, Color border, Color fg, double leftMargin = 0)
    {
        return new Border
        {
            Background = new SolidColorBrush(bg),
            BorderBrush = new SolidColorBrush(border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(leftMargin, 0, 0, 0),
            Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(fg),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            }
        };
    }

    private static Border MakeFeatureCard(string emoji, string title, string desc, Thickness margin)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = emoji, FontSize = 14, Margin = new Thickness(0, 0, 8, 0) });
        var textStack = new StackPanel();
        textStack.Children.Add(new TextBlock { Text = title, Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)), FontSize = 12, FontWeight = FontWeights.Bold });
        textStack.Children.Add(new TextBlock { Text = desc, Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)), FontSize = 10.5, Margin = new Thickness(0, 1, 0, 0) });
        panel.Children.Add(textStack);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x3D)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = margin,
            Child = panel
        };
    }

    private static Border MakeFeatureCardWide(string emoji, string title, string desc)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = emoji, FontSize = 14, Margin = new Thickness(0, 0, 8, 0) });
        var textStack = new StackPanel();
        textStack.Children.Add(new TextBlock { Text = title, Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)), FontSize = 12, FontWeight = FontWeights.Bold });
        textStack.Children.Add(new TextBlock { Text = desc, Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)), FontSize = 10.5, Margin = new Thickness(0, 1, 0, 0) });
        panel.Children.Add(textStack);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x3D)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Child = panel
        };
    }
}
