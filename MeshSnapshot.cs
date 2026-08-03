#region using
using System;
#endregion

#region 定義
using V3d = OpenTK.Mathematics.Vector3d;
using PT = OpenTK.Graphics.OpenGL4.PrimitiveType;
#endregion

namespace Crystallography.OpenGL;

//260803Cl 追加: 3Dプリント (STL/3MF) などの外部エクスポート向けに、GLObject の表示メッシュを
//三角形群 (ワールド座標) として取り出す読み取り専用のスナップショット機能。描画系には一切関与しない。
//設計の全体像はReciProリポの .project-guidance/ReciPro/ReciPro_3Dプリント出力設計.md を参照。

/// <summary>スナップショット元の GLObject 種別 (260803Cl 追加)</summary>
public enum SnapshotKind { Other = 0, Sphere, Ellipsoid, Cylinder, Cone, Pipe, Polyhedron, Torus, Polygon, Lines, Text }

/// <summary>
/// GLObject の表示メッシュのスナップショット (260803Cl 追加).
/// Triangles はワールド座標 (ObjectMatrix 適用済) の三角形頂点列 (3個で1三角形).
/// 生成元プリミティブのパラメータ (球の中心・半径など) も保持し、エクスポート側での
/// 適応的な再テッセレーションや印刷適性チェックを可能にする.
/// </summary>
public sealed class MeshSnapshot
{
    /// <summary>元 GLObject の種別</summary>
    public SnapshotKind Kind;

    /// <summary>元 GLObject の Tag (アプリ側の意味情報)</summary>
    public object Tag;

    /// <summary>元 GLObject の Material の Argb 色</summary>
    public int Argb;

    /// <summary>元 GLObject の Rendered フラグ</summary>
    public bool Rendered;

    /// <summary>三角形の頂点列 (ワールド座標). 3 要素で 1 三角形. 面を持たないオブジェクトでは空</summary>
    public V3d[] Triangles;

    /// <summary>物体の外接球中心 (ワールド座標). 凸形状では面の外向き判定の基準に使える</summary>
    public V3d Center;

    /// <summary>物体の外接球半径</summary>
    public double Radius;

    /// <summary>Kind が Sphere の場合の中心</summary>
    public V3d SphereCenter;

    /// <summary>Kind が Sphere の場合の半径</summary>
    public double SphereRadius;

    /// <summary>Kind が Cylinder/Cone/Pipe の場合の始点</summary>
    public V3d PipeOrigin;

    /// <summary>Kind が Cylinder/Cone/Pipe の場合の始点から終点へのベクトル</summary>
    public V3d PipeVector;

    /// <summary>Kind が Cylinder/Cone/Pipe の場合の始点側・終点側の半径</summary>
    public double PipeRadius1, PipeRadius2;

    /// <summary>
    /// GLObject から表示メッシュのスナップショットを生成する。
    /// 三角形系プリミティブ (Triangles/TriangleStrip/TriangleFan) のみを展開し、線・点は含めない。
    /// 頂点には ObjectMatrix (行ベクトル規約: world = x*Row0 + y*Row1 + z*Row2 + Row3) を適用して返す。
    /// TextObject はビルボード (印刷対象外) のため三角形を展開しない。
    /// </summary>
    public static MeshSnapshot From(GLObject o)
    {
        ArgumentNullException.ThrowIfNull(o);

        var snap = new MeshSnapshot
        {
            Tag = o.Tag,
            Argb = o.Material?.Argb ?? 0,
            Rendered = o.Rendered,
            Center = new V3d(o.CircumscribedSphereCenter.X, o.CircumscribedSphereCenter.Y, o.CircumscribedSphereCenter.Z),
            Radius = o.CircumscribedSphereRadius,
            Triangles = [],
        };

        //派生の深い型から順に判定 (Sphere は Ellipsoid の、Cylinder/Cone は Pipe のサブクラス)
        switch (o)
        {
            case Sphere s: snap.Kind = SnapshotKind.Sphere; snap.SphereCenter = s.Origin; snap.SphereRadius = s.Radius; break;
            case Ellipsoid: snap.Kind = SnapshotKind.Ellipsoid; break;
            case Cylinder cy: snap.Kind = SnapshotKind.Cylinder; snap.PipeOrigin = cy.Origin; snap.PipeVector = cy.Vector; snap.PipeRadius1 = cy.Radius1; snap.PipeRadius2 = cy.Radius2; break;
            case Cone co: snap.Kind = SnapshotKind.Cone; snap.PipeOrigin = co.Origin; snap.PipeVector = co.Vector; snap.PipeRadius1 = co.Radius1; snap.PipeRadius2 = co.Radius2; break;
            case Pipe p: snap.Kind = SnapshotKind.Pipe; snap.PipeOrigin = p.Origin; snap.PipeVector = p.Vector; snap.PipeRadius1 = p.Radius1; snap.PipeRadius2 = p.Radius2; break;
            case Polyhedron: snap.Kind = SnapshotKind.Polyhedron; break;
            case Torus: snap.Kind = SnapshotKind.Torus; break;
            case Lines: snap.Kind = SnapshotKind.Lines; break;
            case TextObject: snap.Kind = SnapshotKind.Text; break;
            case Polygon: snap.Kind = SnapshotKind.Polygon; break;
            default: snap.Kind = SnapshotKind.Other; break;
        }

        if (snap.Kind == SnapshotKind.Text || o.Vertices is not { Length: > 0 } || o.Indices is not { Length: > 0 } || o.Primitives == null)
            return snap;

        //三角形数を先に数えて一括確保
        var triCount = 0;
        var offset = 0;
        foreach (var (type, count) in o.Primitives)
        {
            if (offset + count > o.Indices.Length)
                break;//インデックス配列との不整合 (通常あり得ない) は安全側で打ち切る
            triCount += type switch
            {
                PT.Triangles => count / 3,
                PT.TriangleStrip or PT.TriangleFan => Math.Max(0, count - 2),
                _ => 0,
            };
            offset += count;
        }
        if (triCount == 0)
            return snap;

        //ObjectMatrix は行ベクトル規約 (GL.UniformMatrix4 に transpose=false で渡し、シェーダ側は M*v で作用
        // = C# 側では v*M)。共有単位メッシュ (既定分割数の Sphere/Cylinder) はこの行列でワールドに配置される。
        var m = o.ObjectMatrix;
        V3d r0 = new(m.Row0.X, m.Row0.Y, m.Row0.Z), r1 = new(m.Row1.X, m.Row1.Y, m.Row1.Z),
            r2 = new(m.Row2.X, m.Row2.Y, m.Row2.Z), r3 = new(m.Row3.X, m.Row3.Y, m.Row3.Z);

        var tri = new V3d[triCount * 3];
        int n = 0;
        offset = 0;
        void add(int i)
        {
            var p = o.Vertices[o.Indices[i]].Position;
            tri[n++] = p.X * r0 + p.Y * r1 + p.Z * r2 + r3;
        }
        foreach (var (type, count) in o.Primitives)
        {
            if (offset + count > o.Indices.Length)
                break;
            switch (type)
            {
                case PT.Triangles:
                    for (int i = 0; i + 2 < count; i += 3)
                    { add(offset + i); add(offset + i + 1); add(offset + i + 2); }
                    break;
                case PT.TriangleStrip:
                    //奇数番目の三角形は巻き方向が反転するので頂点順を入れ替えて揃える
                    for (int i = 2; i < count; i++)
                        if ((i & 1) == 0)
                        { add(offset + i - 2); add(offset + i - 1); add(offset + i); }
                        else
                        { add(offset + i - 1); add(offset + i - 2); add(offset + i); }
                    break;
                case PT.TriangleFan:
                    for (int i = 2; i < count; i++)
                    { add(offset); add(offset + i - 1); add(offset + i); }
                    break;
            }
            offset += count;
        }
        snap.Triangles = tri;
        return snap;
    }
}
