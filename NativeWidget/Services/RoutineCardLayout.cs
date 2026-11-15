using System.Windows;

namespace NativeWidget.Services;

internal sealed record RoutineCardPlacement(Rect Bounds, double Angle);
internal static class RoutineCardLayout
{
    // Allocate cards into disjoint bands, then fit their rotated bounding boxes into cells.
    internal static List<RoutineCardPlacement> Place(Size screen, Rect board, int count, int seed)
    {
        var result = new List<RoutineCardPlacement>(); if (count <= 0) return result;
        const double p = 12, gap = 16;
        var regions = new[] { new Rect(p,p,Math.Max(0,screen.Width-2*p),Math.Max(0,board.Top-gap-p)),
            new Rect(p,board.Bottom+gap,Math.Max(0,screen.Width-2*p),Math.Max(0,screen.Height-board.Bottom-gap-p)),
            new Rect(p,board.Top,Math.Max(0,board.Left-gap-p),board.Height),
            new Rect(board.Right+gap,board.Top,Math.Max(0,screen.Width-board.Right-gap-p),board.Height) }
            .Where(r => r.Width > 2 && r.Height > 2).ToArray();
        var area = regions.Sum(r => r.Width * r.Height); if (area == 0) return result;
        var counts = new int[regions.Length];
        static double Fit(Rect r,int n)
        {double best=0;for(var cols=1;cols<=n;cols++)best=Math.Max(best,Math.Min(260,Math.Min(r.Width/cols,r.Height/Math.Ceiling(n/(double)cols)*1.1)));return Math.Round(best,1);}
        // Prefer the largest readable cards rather than putting a tiny card in a
        // shallow top band merely because that band has a large area.
        for(var i=0;i<count;i++)
        {var best=Enumerable.Range(0,regions.Length).OrderByDescending(k=>Fit(regions[k],counts[k]+1)).ThenBy(k=>counts[k]).First();counts[best]++;}
        var random = new Random(seed);
        for (var k=0;k<regions.Length;k++)
        {
            var n=counts[k]; if(n==0)continue; var r=regions[k]; var cols=1; var rows=n; double score=0;
            for(var c=1;c<=n;c++){var rr=(int)Math.Ceiling(n/(double)c);var candidate=Math.Min(r.Width/c,r.Height/rr*1.1);if(candidate>score){score=candidate;cols=c;rows=rr;}}
            var cw=r.Width/cols;var ch=r.Height/rows;
            for(var j=0;j<n;j++)
            {
                var angle=(random.NextDouble()-.5)*12;var theta=angle*Math.PI/180;var cos=Math.Abs(Math.Cos(theta));var sin=Math.Abs(Math.Sin(theta));
                var w=Math.Min(260,cw*.88);var h=w/1.1;var scale=Math.Min(1,Math.Min(cw*.9/(w*cos+h*sin),ch*.9/(w*sin+h*cos)));w*=scale;h*=scale;
                var bw=w*cos+h*sin;var bh=w*sin+h*cos;
                var cx=r.Left+(j%cols+.5)*cw+(random.NextDouble()-.5)*(cw-bw)*.4;
                var cy=r.Top+(j/cols+.5)*ch+(random.NextDouble()-.5)*(ch-bh)*.4;
                result.Add(new RoutineCardPlacement(new Rect(cx-w/2,cy-h/2,w,h),angle));
            }
        }
        return result;
    }
}
