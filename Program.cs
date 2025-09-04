using Discord;
using Discord.WebSocket;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

class Program
{
    private DiscordSocketClient _client;
    private SheetsService _sheetsService;

    // Define par for courses by base course name
    private Dictionary<string, int[]> _coursePars = new Dictionary<string, int[]>
    {
        { "Charlotte National", new int[] { 4, 3, 5, 4, 4, 3, 5, 4, 4 } },
        // Add more courses here as needed
    };

    private string spreadsheetId = "11P4OVS0asd8PWqJlkoIJUuQkQwfViYHerczDDeg7rYg"; // Replace with your Google Sheet ID

    public static Task Main(string[] args) => new Program().MainAsync();

    public async Task MainAsync()
    {
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds |
                             GatewayIntents.GuildMessages |
                             GatewayIntents.MessageContent
        });

        _client.Log += Log;

        string? token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine("❌ Bot token is missing! Set DISCORD_TOKEN environment variable.");
            return;
        }
        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        _client.MessageReceived += HandleMessageAsync;

        InitializeSheetsService();

        // Start leaderboard timer
        _ = StartLeaderboardTimer(); // Fire-and-forget

        await Task.Delay(-1); // Keep bot running
    }

    private Task Log(LogMessage msg)
    {
        Console.WriteLine(msg.ToString());
        return Task.CompletedTask;
    }

    private void InitializeSheetsService()
    {
        string[] scopes = { SheetsService.Scope.SpreadsheetsReadonly };

        string? json = Environment.GetEnvironmentVariable("GOOGLE_CREDENTIALS");
        if (string.IsNullOrEmpty(json))
        {
            Console.WriteLine("❌ GOOGLE_CREDENTIALS environment variable is missing!");
            return;
        }

        GoogleCredential credential;
        using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
        {
            credential = GoogleCredential.FromStream(stream).CreateScoped(scopes);
        }

        _sheetsService = new SheetsService(new BaseClientService.Initializer()
        {
            HttpClientInitializer = credential,
            ApplicationName = "GolfScoreBot"
        });
    }


    private async Task HandleMessageAsync(SocketMessage rawMessage)
    {
        if (rawMessage is not SocketUserMessage message) return;
        if (message.Author.IsBot) return;

        var content = message.Content.Replace("\n", " ").Replace("\r", "").Trim();
        if (string.IsNullOrEmpty(content)) return;

        var parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        string command = parts[0].ToLower();

        switch (command)
        {
            case "!leaderboard":
                await PostLeaderboardAsync(message.Channel as IMessageChannel, parts);
                break;

            case "!ping":
                await message.Channel.SendMessageAsync($"Pong! Latency: {_client.Latency}ms");
                break;
        }
    }

    private string NormalizeCourseName(string sheetName)
    {
        // Remove trailing year like '25 or '23 etc.
        var idx = sheetName.LastIndexOf('\'');
        if (idx > 0)
            sheetName = sheetName.Substring(0, idx).Trim();

        return sheetName;
    }

    // Timer: posts leaderboard automatically 4x per hour
    private async Task StartLeaderboardTimer()
    {
        ulong channelId = 783808528439705664; // Replace with your target channel ID
        var channel = _client.GetChannel(channelId) as IMessageChannel;
        if (channel == null)
        {
            Console.WriteLine("Invalid channel ID for leaderboard timer.");
            return;
        }

        while (true)
        {
            try
            {
                await PostLeaderboardAsync(channel);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error posting leaderboard: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromMinutes(60)); // 1 time an hour
        }
    }

    // Post leaderboard, can be called from command or timer
    private async Task PostLeaderboardAsync(IMessageChannel channel, string[] commandParts = null)
    {
        // Determine which sheet/tab to read
        string gameName;
        if (commandParts != null && commandParts.Length > 1)
        {
            gameName = string.Join(' ', commandParts[1..]);
        }
        else
        {
            var spreadsheet = await _sheetsService.Spreadsheets.Get(spreadsheetId).ExecuteAsync();
            if (spreadsheet.Sheets.Count == 0)
            {
                await channel.SendMessageAsync("No sheets found in the spreadsheet.");
                return;
            }
            gameName = spreadsheet.Sheets[0].Properties.Title;
        }

        string normalizedName = NormalizeCourseName(gameName);

        var request = _sheetsService.Spreadsheets.Values.Get(spreadsheetId, gameName);
        ValueRange response;
        try
        {
            response = await request.ExecuteAsync();
        }
        catch
        {
            await channel.SendMessageAsync($"No sheet/tab found with the name `{gameName}`.");
            return;
        }

        if (response.Values == null || response.Values.Count == 0)
        {
            await channel.SendMessageAsync($"No scores found in `{gameName}`.");
            return;
        }

        // First row = headers (Team Name, Hole 1, Hole 2, ...)
        var headers = response.Values[0].Select(h => h.ToString()).ToArray();
        int numHoles = headers.Length - 1;

        var leaderboard = new List<(string Team, List<int> StrokesPerHole)>();
        for (int i = 1; i < response.Values.Count; i++)
        {
            var row = response.Values[i];
            if (row.Count == 0) continue;

            string teamName = row[0].ToString();
            var strokes = new List<int>();
            for (int h = 1; h <= numHoles; h++)
            {
                if (h < row.Count && int.TryParse(row[h].ToString(), out int score))
                    strokes.Add(score);
            }
            leaderboard.Add((teamName, strokes));
        }

        // Calculate per-hole par difference
        var displayList = leaderboard.Select(entry =>
        {
            int total = entry.StrokesPerHole.Sum();
            int holesPlayed = entry.StrokesPerHole.Count;
            string parText = "";

            if (_coursePars.TryGetValue(normalizedName, out int[] pars))
            {
                int diff = 0;
                for (int h = 0; h < holesPlayed; h++)
                {
                    int par = h < pars.Length ? pars[h] : 0;
                    diff += entry.StrokesPerHole[h] - par;
                }

                if (diff == 0) parText = "(E)";
                else if (diff > 0) parText = $"(+{diff})";
                else parText = $"({diff})";
            }

            return (entry.Team, total, holesPlayed, parText);
        }).OrderBy(t => t.total).ToList();

        // Build Discord message with code block formatting
        string msg = $"**🏌️ Leaderboard for {gameName} 🏌️**\n```";
        int rank = 1;
        foreach (var entry in displayList)
        {
            msg += $"{rank,2}. {entry.Team,-15} {entry.total,3} strokes ({entry.holesPlayed} holes) {entry.parText}\n";
            rank++;
        }
        msg += "```";

        await channel.SendMessageAsync(msg);
    }
}
