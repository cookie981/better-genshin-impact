using BgiCoordinatorServer.Models;
using System;

namespace BgiCoordinatorServer.Tests;

public class WaitPointCoordinationTests
{
    public static void TestCoordinateWaitPoints()
    {
        Console.WriteLine("=== 测试等待点协调逻辑 ===");
        
        // 测试1：单个等待点
        Console.WriteLine("\n测试1：单个等待点");
        var report1 = new WaitPointReport("player1", "Route_1", "sync_1_1", 1);
        var coordinated1 = CoordinatedWaitPoint.Coordinate(new[] { report1 });
        if (coordinated1 != null)
        {
            Console.WriteLine($"协调结果：路线={coordinated1.RouteId}, 同步点={coordinated1.SyncPointId}, 对齐玩家={string.Join(",", coordinated1.AlignedPlayers)}");
        }
        else
        {
            Console.WriteLine("协调失败");
        }
        
        // 测试2：多个等待点，相同路线
        Console.WriteLine("\n测试2：多个等待点，相同路线");
        var report2a = new WaitPointReport("player1", "Route_2", "sync_2_1", 1);
        var report2b = new WaitPointReport("player2", "Route_2", "sync_2_2", 1);
        var coordinated2 = CoordinatedWaitPoint.Coordinate(new[] { report2a, report2b });
        if (coordinated2 != null)
        {
            Console.WriteLine($"协调结果：路线={coordinated2.RouteId}, 同步点={coordinated2.SyncPointId}, 对齐玩家={string.Join(",", coordinated2.AlignedPlayers)}");
            Console.WriteLine($"预期：应该选择sync_2_1（同步点标识最小）");
        }
        
        // 测试3：多个等待点，不同路线（最落后玩家原则）
        Console.WriteLine("\n测试3：多个等待点，不同路线（最落后玩家原则）");
        var report3a = new WaitPointReport("player1", "Route_1", "sync_1_1", 1);
        var report3b = new WaitPointReport("player2", "Route_2", "sync_2_1", 1);
        var report3c = new WaitPointReport("player3", "Route_3", "sync_3_1", 1);
        var coordinated3 = CoordinatedWaitPoint.Coordinate(new[] { report3a, report3b, report3c });
        if (coordinated3 != null)
        {
            Console.WriteLine($"协调结果：路线={coordinated3.RouteId}, 同步点={coordinated3.SyncPointId}, 对齐玩家={string.Join(",", coordinated3.AlignedPlayers)}");
            Console.WriteLine($"预期：应该选择Route_1（路线索引最小，最落后）");
        }
        
        // 测试4：过期等待点
        Console.WriteLine("\n测试4：过期等待点");
        var report4 = new WaitPointReport("player1", "Route_1", "sync_1_1", 1);
        report4.ExpiryTime = DateTime.UtcNow.AddSeconds(-10); // 设置为已过期
        var coordinated4 = CoordinatedWaitPoint.Coordinate(new[] { report4 });
        if (coordinated4 == null)
        {
            Console.WriteLine("协调结果：null（正确，过期等待点应被忽略）");
        }
        else
        {
            Console.WriteLine("协调失败：过期等待点不应被协调");
        }
        
        // 测试5：无效等待点
        Console.WriteLine("\n测试5：无效等待点");
        var report5 = new WaitPointReport("", "Route_1", "sync_1_1", 1); // 空的PlayerUid
        var coordinated5 = CoordinatedWaitPoint.Coordinate(new[] { report5 });
        if (coordinated5 == null)
        {
            Console.WriteLine("协调结果：null（正确，无效等待点应被忽略）");
        }
        else
        {
            Console.WriteLine("协调失败：无效等待点不应被协调");
        }
        
        Console.WriteLine("\n=== 测试完成 ===");
    }
    
    public static void TestWaitPointReportValidation()
    {
        Console.WriteLine("\n=== 测试等待点验证逻辑 ===");
        
        // 测试有效等待点
        var validReport = new WaitPointReport("player1", "Route_1", "sync_1_1", 1);
        Console.WriteLine($"有效等待点验证：{validReport.IsValid()} (应为True)");
        
        // 测试无效等待点（空PlayerUid）
        var invalidReport1 = new WaitPointReport("", "Route_1", "sync_1_1", 1);
        Console.WriteLine($"无效等待点验证（空PlayerUid）：{invalidReport1.IsValid()} (应为False)");
        
        // 测试无效等待点（空RouteId）
        var invalidReport2 = new WaitPointReport("player1", "", "sync_1_1", 1);
        Console.WriteLine($"无效等待点验证（空RouteId）：{invalidReport2.IsValid()} (应为False)");
        
        // 测试无效等待点（空SyncPointId）
        var invalidReport3 = new WaitPointReport("player1", "Route_1", "", 1);
        Console.WriteLine($"无效等待点验证（空SyncPointId）：{invalidReport3.IsValid()} (应为False)");
        
        // 测试无效等待点（负轮次）
        var invalidReport4 = new WaitPointReport("player1", "Route_1", "sync_1_1", -1);
        Console.WriteLine($"无效等待点验证（负轮次）：{invalidReport4.IsValid()} (应为False)");
        
        Console.WriteLine("=== 验证测试完成 ===");
    }
    
    public static void Main()
    {
        TestCoordinateWaitPoints();
        TestWaitPointReportValidation();
    }
}