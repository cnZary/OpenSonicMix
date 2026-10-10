using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Controls;

/// <summary>
/// 音量推子(见需求 10.3/ui_controls_report 1.1)。
/// 协议 dB(-51~+10)经分段线性映射到 UI 等距行程;拖动合并发送;双击复位 0;支持手输。
/// </summary>
public sealed class VolumeFader : Control
{
    private double _db;                 // 协议值 dB
    private bool _dragging;
    private float _lastSentDb = float.NaN;
    private readonly System.Windows.Forms.Timer _sendTimer;

    /// <summary>拖动过程中节流后待发送的最新 dB。</summary>
    private float _pendingDb = float.NaN;

    public event Action<float>? ValueCommitted;   // 提交协议 dB(节流后)
    public event Action<float>? ValueChanging;    // 拖动中即时显示

    public double MinDb { get; } = -51;
    public double MaxDb { get; } = 10;

    public VolumeFader()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Width = 56; Height = 180;
        _sendTimer = new System.Windows.Forms.Timer { Interval = 50 };
        _sendTimer.Tick += (_, _) => FlushPending();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public double Db
    {
        get => _db;
        set
        {
            value = Math.Clamp(value, MinDb, MaxDb);
            if (Math.Abs(_db - value) < 1e-6) return;
            _db = value;
            Invalidate();
        }
    }

    public void SetDbSilently(double db)
    {
        db = Math.Clamp(db, MinDb, MaxDb);
        if (Math.Abs(_db - db) < 1e-6) return;
        _db = db;
        Invalidate();
    }

    private double TrackTop => 8;
    private double TrackHeight => Height - 40;

    private double DbToY(double db)
    {
        double pos = ValueMapping.FaderDbToPosition(db); // UI 等距位置(-51..10)
        double t = (pos - MinDb) / (MaxDb - MinDb);      // 0..1
        return TrackTop + (1 - t) * TrackHeight;
    }

    private double YToDb(double y)
    {
        double t = 1 - (y - TrackTop) / TrackHeight;
        double pos = MinDb + t * (MaxDb - MinDb);
        return ValueMapping.FaderPositionToDb(pos);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        // 轨道
        var trackRect = new RectangleF(Width / 2f - 3, (float)TrackTop, 6, (float)TrackHeight);
        using (var trackBrush = new SolidBrush(Color.FromArgb(60, 60, 66)))
            g.FillRectangle(trackBrush, trackRect);
        // 刻度
        using var tickPen = new Pen(Color.FromArgb(90, 90, 96));
        foreach (double db in new[] { 10.0, 5, 0, -5, -10, -20, -30, -51 })
        {
            float y = (float)DbToY(db);
            g.DrawLine(tickPen, Width / 2f + 5, y, Width / 2f + 11, y);
        }
        // 滑块
        float thumbY = (float)DbToY(_db);
        var thumb = new RectangleF(Width / 2f - 16, thumbY - 7, 32, 14);
        using (var thumbBrush = new SolidBrush(Color.FromArgb(0, 122, 204)))
            g.FillRoundedRectangle(thumbBrush, thumb, 3);
        using (var thumbPen = new Pen(Color.FromArgb(30, 160, 240)))
            g.DrawRoundedRectangle(thumbPen, thumb, 3);
        // 数值文本
        string txt = ValueMapping.FaderText(_db);
        using var textBrush = new SolidBrush(ForeColor);
        var fmt = new StringFormat { Alignment = StringAlignment.Center };
        g.DrawString(txt, Font, textBrush, new RectangleF(0, Height - 26, Width, 22), fmt);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            Capture = true;
            UpdateFromY(e.Y);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) UpdateFromY(e.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging)
        {
            _dragging = false;
            Capture = false;
            FlushPending();
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left)
        {
            Db = 0;
            QueueSend(0);
            FlushPending();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        double delta = e.Delta > 0 ? 1 : -1;
        double nd = Math.Clamp(_db + delta, MinDb, MaxDb);
        Db = nd;
        QueueSend((float)nd);
        FlushPending();
    }

    private void UpdateFromY(int y)
    {
        double nd = Math.Clamp(YToDb(y), MinDb, MaxDb);
        if (Math.Abs(nd - _db) < 0.05) return;
        Db = nd;
        ValueChanging?.Invoke((float)nd);
        QueueSend((float)nd);
    }

    private void QueueSend(float db)
    {
        _pendingDb = db;
        if (!_sendTimer.Enabled) _sendTimer.Start();
    }

    private void FlushPending()
    {
        _sendTimer.Stop();
        if (float.IsNaN(_pendingDb)) return;
        if (Math.Abs(_pendingDb - _lastSentDb) < 0.01f) { _pendingDb = float.NaN; return; }
        _lastSentDb = _pendingDb;
        _pendingDb = float.NaN;
        ValueCommitted?.Invoke(_lastSentDb);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _sendTimer.Dispose();
        base.Dispose(disposing);
    }
}

internal static class GraphicsExt
{
    public static void FillRoundedRectangle(this Graphics g, Brush b, RectangleF r, float radius)
    {
        using var path = RoundedRect(r, radius);
        g.FillPath(b, path);
    }

    public static void DrawRoundedRectangle(this Graphics g, Pen p, RectangleF r, float radius)
    {
        using var path = RoundedRect(r, radius);
        g.DrawPath(p, path);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        float d = radius * 2;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
