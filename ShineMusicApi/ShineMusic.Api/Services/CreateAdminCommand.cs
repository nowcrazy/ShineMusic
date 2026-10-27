// --create-admin 命令处理服务：系统首个账号入口（注册采用邀请制）。
// 仅在库中还没有任何用户时允许执行，防止该参数被重复利用来越权追加管理员。
// 支持两种模式：
//   1) 环境变量非交互模式：ADMIN_EMAIL / ADMIN_USERNAME / ADMIN_NAME / ADMIN_PASSWORD
//   2) 交互模式：未设置环境变量时，控制台提示输入
// 流程结束后由调用方决定是否退出进程（Program.cs 中执行完 return，不启动 Web 主机）。

using System.Text;
using ShineMusic.Model.Entities;
using ShineMusic.Service;
using SqlSugar;

namespace ShineMusic.Api.Services;

/// <summary>首位管理员创建命令。</summary>
public class CreateAdminCommand
{
    private readonly ISqlSugarClient _sqlSugar;
    private readonly UserService _userService;

    public CreateAdminCommand(ISqlSugarClient sqlSugar, UserService userService)
    {
        _sqlSugar = sqlSugar;
        _userService = userService;
    }

    /// <summary>
    /// 执行管理员创建。返回 true 表示成功，false 表示失败（已向 Console.Error 输出原因）。
    /// </summary>
    public async Task<bool> ExecuteAsync()
    {
        if (await _userService.AnyUsersAsync())
        {
            Console.Error.WriteLine("Admin initialization is only allowed before the first user exists.");
            return false;
        }

        // 优先从环境变量读取（非交互/自动化场景），缺失则回退到控制台交互输入。
        // username 与 email 均为登录凭证（登录时可二选一 + password），需分别设置；
        // displayName 为昵称，仅用于展示。
        var email = Environment.GetEnvironmentVariable("ADMIN_EMAIL");
        var userName = Environment.GetEnvironmentVariable("ADMIN_USERNAME");
        var displayName = Environment.GetEnvironmentVariable("ADMIN_NAME");
        var password = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");

        if (string.IsNullOrWhiteSpace(email))
        {
            Console.Write("Admin email: ");
            email = Console.ReadLine()?.Trim();
        }
        if (string.IsNullOrWhiteSpace(userName))
        {
            Console.Write("Admin username (login name): ");
            userName = Console.ReadLine()?.Trim();
        }
        if (string.IsNullOrWhiteSpace(displayName))
        {
            Console.Write("Display name (昵称): ");
            displayName = Console.ReadLine()?.Trim();
        }
        if (string.IsNullOrWhiteSpace(password))
        {
            Console.Write("Password: ");
            password = ReadPassword();
            Console.WriteLine();
        }
        // username 与 email 均为登录凭证且各自唯一，必须分别填写，不做 fallback。
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine("Email, username, display name and password are required.");
            return false;
        }

        var admin = new ApplicationUser
        {
            Email = email,
            UserName = userName,
            DisplayName = displayName,
            EmailConfirmed = true
        };
        // 建用户与授予 admin 角色放在同一事务内，任一步失败都整体回滚，避免留下无角色的残留账号。
        await _sqlSugar.Ado.BeginTranAsync();
        try
        {
            var createResult = await _userService.CreateAsync(admin, password);
            if (!createResult.Succeeded)
            {
                await _sqlSugar.Ado.RollbackTranAsync();
                Console.Error.WriteLine(string.Join(Environment.NewLine, createResult.Errors.Select(error => error.Description)));
                return false;
            }

            var roleResult = await _userService.AddToRoleAsync(admin.Id, "admin");
            if (!roleResult.Succeeded)
            {
                await _sqlSugar.Ado.RollbackTranAsync();
                Console.Error.WriteLine(string.Join(Environment.NewLine, roleResult.Errors.Select(error => error.Description)));
                return false;
            }

            await _sqlSugar.Ado.CommitTranAsync();
        }
        catch
        {
            await _sqlSugar.Ado.RollbackTranAsync();
            throw;
        }
        Console.WriteLine($"Initial administrator created: {admin.UserName} ({admin.Email}).");
        return true;
    }

    /// <summary>控制台无回显读取密码（拦截按键、支持退格）。</summary>
    private static string ReadPassword()
    {
        var password = new StringBuilder();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace && password.Length > 0)
                password.Length--;
            else if (!char.IsControl(key.KeyChar))
                password.Append(key.KeyChar);
        }
        return password.ToString();
    }
}
