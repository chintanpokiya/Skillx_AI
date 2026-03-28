using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using new_mvc.Models;
using Npgsql;

namespace new_mvc.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;

    private readonly IConfiguration _config;

    public HomeController(ILogger<HomeController> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    private string GetConnection()
    {
        return _config.GetConnectionString("DefaultConnection");
    }

    public IActionResult Index()
    {
        return View();
    }

    public IActionResult Moderate()
    {
        return View();
    }
    public IActionResult Recommendation()
    {
        string domains = "";

        using (var conn = new NpgsqlConnection(GetConnection()))
        {
            conn.Open();

            using (var cmd = new NpgsqlCommand("SELECT domains FROM users WHERE email=@email", conn))
            {
                string email = HttpContext.Session.GetString("email");

                if (string.IsNullOrEmpty(email))
                    return RedirectToAction("Login");

                cmd.Parameters.AddWithValue("@email", email);

                var result = cmd.ExecuteScalar();
                domains = result?.ToString() ?? "";
            }
        }

        ViewBag.Domains = domains;

        return View();
    }
    public async Task<IActionResult> AllPosts()
    {
        var posts = new List<dynamic>();
        
        using (var conn = new NpgsqlConnection(GetConnection()))
        {
            await conn.OpenAsync();

            var query = "SELECT id, content, tags FROM posts";

            using (var cmd = new NpgsqlCommand(query, conn))
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    posts.Add(new
                    {
                        Id = reader.GetInt32(0),
                        Content = reader.GetString(1),
                        Tags = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    });
                }
            }
        }

        return View(posts);
    }

    public IActionResult Register()
    {
        return View();
    }

    public IActionResult Login()
    {
        return View();
    }

    [HttpPost]
    public IActionResult LoginUser([FromBody] vm_login user)
    {
        try
        {
            using (var conn = new NpgsqlConnection(GetConnection()))
            {
                conn.Open();

                using (var cmd = new NpgsqlCommand(@"SELECT id, email 
                                                 FROM users 
                                                 WHERE email=@email AND password=@password", conn))
                {
                    cmd.Parameters.AddWithValue("@email", user.Email ?? "");
                    cmd.Parameters.AddWithValue("@password", user.Password ?? "");

                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            // ✅ Store session
                            HttpContext.Session.SetString("email", reader["email"].ToString());
                            HttpContext.Session.SetInt32("userId", Convert.ToInt32(reader["id"]));

                            return Ok(new
                            {
                                success = true,
                                message = "Login successful"
                            });
                        }
                        else
                        {
                            return Ok(new
                            {
                                success = false,
                                message = "Invalid email or password"
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }
    public IActionResult Privacy()
    {
        return View();
    }

    [HttpPost]
    public IActionResult Create([FromBody] User user)
    {
        using (var conn = new NpgsqlConnection(GetConnection()))
        {
            conn.Open();

            using (var checkCmd = new NpgsqlCommand("SELECT COUNT(*) FROM users WHERE email=@email", conn))
            {
                checkCmd.Parameters.AddWithValue("@email", user.Email);
                int count = Convert.ToInt32(checkCmd.ExecuteScalar());

                if (count > 0)
                    return BadRequest("Email already exists");
            }

            using (var cmd = new NpgsqlCommand(@"INSERT INTO users 
                (name, gender, dob, email, password, domains) 
                VALUES (@name, @gender, @dob, @email, @password, @domains)", conn))
            {
                cmd.Parameters.AddWithValue("@name", user.Name ?? "");
                cmd.Parameters.AddWithValue("@gender", user.Gender ?? "");
                cmd.Parameters.AddWithValue("@dob", user.Dob);
                cmd.Parameters.AddWithValue("@email", user.Email ?? "");
                cmd.Parameters.AddWithValue("@password", user.Password ?? "");
                cmd.Parameters.AddWithValue("@domains", user.Domains ?? "");

                cmd.ExecuteNonQuery();
            }
        }

        return Ok(new { message = "User registered successfully" });
    }

    public IActionResult Post()
    {
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Post(string text)
    {
        List<string> tags = new List<string>();
        Dictionary<string, double> moderation = new Dictionary<string, double>();
        Boolean success = false;


        using (HttpClient client = new HttpClient())
        {
            // ------------------ MODERATION API ------------------
            var json = JsonSerializer.Serialize(new { text = text });

            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var modResponse = await client.PostAsync(
                "http://127.0.0.1:8000/moderate",
                content
            );
            if (modResponse.IsSuccessStatusCode)
            {
                var modJson = await modResponse.Content.ReadAsStringAsync();

                var doc = JsonDocument.Parse(modJson);

                // is_safe
                success = doc.RootElement.GetProperty("is_safe").GetBoolean();

                // moderation object
                var modObj = doc.RootElement.GetProperty("moderation");

                foreach (var item in modObj.EnumerateObject())
                {
                    moderation[item.Name] = item.Value.GetDouble();
                }
            }
        }
        if (!success)
        {
            return Ok(new { success = false, moderation = moderation });
        }



        using (HttpClient client = new HttpClient())
        {
            var json = JsonSerializer.Serialize(new { text = text });

            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(
                "http://127.0.0.1:8000/tag",
                content
            );

            if (response.IsSuccessStatusCode)
            {
                var json1 = await response.Content.ReadAsStringAsync();

                var doc = JsonDocument.Parse(json1);

                tags = doc.RootElement
        .GetProperty("predicted_tags")
        .EnumerateArray()
        .Select(x => x.GetProperty("tag").GetString())
        .ToList();
            }
        }


        // ================= SAVE TO DATABASE =================


        using (var conn = new NpgsqlConnection(GetConnection()))
        {
            await conn.OpenAsync();

            var query = @"INSERT INTO posts (content, tags) VALUES (@content, @tags); ";

            using (var cmd = new NpgsqlCommand(query, conn))
            {
                cmd.Parameters.AddWithValue("content", text);
                cmd.Parameters.AddWithValue("tags", string.Join(",", tags)); // "ai,ml,python"

                await cmd.ExecuteNonQueryAsync();
            }
        }

        return Ok(new { success = true });
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
