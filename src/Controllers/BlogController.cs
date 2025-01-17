namespace Miniblog.Core.Controllers
{
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Options;

    using Miniblog.Core.Models;
    using Miniblog.Core.Services;

    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Net;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using System.Xml;

    using WebEssentials.AspNetCore.Pwa;

    public class BlogController : Controller
    {
        private readonly IBlogService blog;

        private readonly WebManifest manifest;

        private readonly IOptionsSnapshot<BlogSettings> settings;

        public BlogController(IBlogService blog, IOptionsSnapshot<BlogSettings> settings, WebManifest manifest)
        {
            this.blog = blog;
            this.settings = settings;
            this.manifest = manifest;
        }

        [Route("/blog/comment/{postId}")]
        [HttpPost]
        public async Task<IActionResult> AddComment(string postId, Comment comment)
        {
            var post = await this.blog.GetPostById(postId).ConfigureAwait(true);

            if (!this.ModelState.IsValid)
            {
                return this.View(nameof(Post), post);
            }

            if (post is null || !post.AreCommentsOpen(this.settings.Value.CommentsCloseAfterDays))
            {
                return this.NotFound();
            }

            if (comment is null)
            {
                throw new ArgumentNullException(nameof(comment));
            }

            comment.IsAdmin = this.User.Identity.IsAuthenticated;
            comment.Content = comment.Content.Trim();
            comment.Author = comment.Author.Trim();
            comment.Email = comment.Email.Trim();

            // the website form key should have been removed by javascript unless the comment was
            // posted by a spam robot
            if (!this.Request.Form.ContainsKey("website"))
            {
                post.Comments.Add(comment);
                await this.blog.SavePost(post).ConfigureAwait(false);
            }

            return Redirect(Url.RouteUrl(new { action = nameof(Post), slug = post.Slug }) + "#comments");
        }

        [Route("/blog/category/{category}/{page:int?}")]
        [OutputCache(Profile = "default")]
        public async Task<IActionResult> Category(string category, int page = 0)
        {
            // get posts for the selected category.
            var posts = this.blog.GetPostsByCategory(category);

            // apply paging filter.
            var filteredPosts = posts.Skip(this.settings.Value.PostsPerPage * page).Take(this.settings.Value.PostsPerPage);

            // set the view option
            this.ViewData["ViewOption"] = this.settings.Value.ListView;

            this.ViewData[Constants.TotalPostCount] = await posts.CountAsync().ConfigureAwait(true);
            this.ViewData[Constants.Title] = $"{this.manifest.Name} {category}";
            this.ViewData[Constants.Description] = $"Articles posted in the {category} category";
            this.ViewData[Constants.prev] = $"/blog/category/{WebUtility.UrlEncode(category)}/{page + 1}/";
            this.ViewData[Constants.next] = $"/blog/category/{WebUtility.UrlEncode(category)}/{(page <= 1 ? null : page - 1 + "/")}";
            this.ViewData[Constants.CatFilter] = category;
            return this.View("~/Views/Blog/Index.cshtml", filteredPosts.AsAsyncEnumerable());
        }

        [Route("/blog/tag/{tag}/{page:int?}")]
        [OutputCache(Profile = "default")]
        public async Task<IActionResult> Tag(string tag, int page = 0)
        {
            // get posts for the selected tag.
            var posts = this.blog.GetPostsByTag(tag);

            // apply paging filter.
            var filteredPosts = posts.Skip(this.settings.Value.PostsPerPage * page).Take(this.settings.Value.PostsPerPage);

            // set the view option
            this.ViewData["ViewOption"] = this.settings.Value.ListView;

            this.ViewData[Constants.TotalPostCount] = await posts.CountAsync().ConfigureAwait(true);
            this.ViewData[Constants.Title] = $"{this.manifest.Name} {tag}";
            this.ViewData[Constants.Description] = $"Articles posted in the {tag} tag";
            this.ViewData[Constants.prev] = $"/blog/tag/{WebUtility.UrlEncode(tag)}/{page + 1}/";
            this.ViewData[Constants.next] = $"/blog/tag/{WebUtility.UrlEncode(tag)}/{(page <= 1 ? null : page - 1 + "/")}";
            this.ViewData[Constants.TagFilter] = tag;
            return this.View("~/Views/Blog/Index.cshtml", filteredPosts.AsAsyncEnumerable());
        }

        [Route("/blog/comment/{postId}/{commentId}")]
        [Authorize]
        public async Task<IActionResult> DeleteComment(string postId, string commentId)
        {
            var post = await this.blog.GetPostById(postId).ConfigureAwait(false);

            if (post is null)
            {
                return this.NotFound();
            }

            var comment = post.Comments.FirstOrDefault(c => c.ID.Equals(commentId, StringComparison.OrdinalIgnoreCase));

            if (comment is null)
            {
                return this.NotFound();
            }

            post.Comments.Remove(comment);
            await this.blog.SavePost(post).ConfigureAwait(false);

            return Redirect(Url.RouteUrl(new { action = nameof(Post), slug = post.Slug }) + "#comments");
        }

        [Route("/blog/deletepost/{id}")]
        [HttpPost, Authorize, AutoValidateAntiforgeryToken]
        public async Task<IActionResult> DeletePost(string id)
        {
            var existing = await this.blog.GetPostById(id).ConfigureAwait(false);
            if (existing is null)
            {
                return this.NotFound();
            }

            await this.blog.DeletePost(existing).ConfigureAwait(false);
            return this.Redirect(Url.Content("~/"));
        }

        [Route("/blog/edit/{id?}")]
        [HttpGet, Authorize]
        public async Task<IActionResult> Edit(string? id)
        {
            var categories = await this.blog.GetCategories().ToListAsync();
            categories.Sort();
            this.ViewData[Constants.AllCats] = categories;

            var tags = await this.blog.GetTags().ToListAsync();
            tags.Sort();
            this.ViewData[Constants.AllTags] = tags;

            if (string.IsNullOrEmpty(id))
            {
                return this.View(new Post());
            }

            var post = await this.blog.GetPostById(id).ConfigureAwait(false);

            return post is null ? this.NotFound() : (IActionResult)this.View(post);
        }

        [Route("/blog/canceledit/{id?}")]
        [HttpPost, Authorize]
        public async Task<IActionResult> CancelEdit(string? id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return RedirectToAction("Index");
            }

            var post = await this.blog.GetPostById(id).ConfigureAwait(false);
            if (post == null)
            {
                return RedirectToAction("Index");
            }

            return RedirectToAction("Post", new { slug = post.Slug });
        }

        [Route("/{page:int?}")]
        [OutputCache(Profile = "default")]
        public async Task<IActionResult> Index([FromRoute]int page = 0)
        {
            // get published posts.
            var posts = this.blog.GetPosts();

            // apply paging filter.
            var filteredPosts = posts.Skip(this.settings.Value.PostsPerPage * page).Take(this.settings.Value.PostsPerPage);

            // set the view option
            this.ViewData[Constants.ViewOption] = this.settings.Value.ListView;

            this.ViewData[Constants.TotalPostCount] = await posts.CountAsync().ConfigureAwait(true);
            this.ViewData[Constants.Title] = this.manifest.Name;
            this.ViewData[Constants.Description] = this.manifest.Description;
            this.ViewData[Constants.prev] = $"/{page + 1}/";
            this.ViewData[Constants.next] = $"/{(page <= 1 ? null : $"{page - 1}/")}";

            return this.View("~/Views/Blog/Index.cshtml", filteredPosts);
        }

        [Route("/blog/{slug?}")]
        [OutputCache(Profile = "default")]
        public async Task<IActionResult> Post(string slug)
        {
            var post = await this.blog.GetPostBySlug(slug).ConfigureAwait(true);

            return post is null ? this.NotFound() : (IActionResult)this.View(post);
        }

        /// <remarks>This is for redirecting potential existing URLs from the old Miniblog URL format.</remarks>
        [Route("/post/{slug}")]
        [HttpGet]
        public IActionResult Redirects(string slug) => this.LocalRedirectPermanent($"~/blog/{slug}");

        [Route("/blog/{slug?}")]
        [HttpPost, Authorize, AutoValidateAntiforgeryToken]
        [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Consumer preference.")]
        public async Task<IActionResult> UpdatePost(Post post)
        {
            if (!this.ModelState.IsValid)
            {
                return this.View(nameof(Edit), post);
            }

            if (post is null)
            {
                throw new ArgumentNullException(nameof(post));
            }

            var existing = await this.blog.GetPostById(post.ID).ConfigureAwait(false) ?? post;
            string categories = this.Request.Form[Constants.categories];
            string tags = this.Request.Form[Constants.tags];

            existing.Categories.Clear();
            categories.Split(",", StringSplitOptions.RemoveEmptyEntries)
                .Select(c => c.Trim().ToLowerInvariant())
                .ToList()
                .ForEach(existing.Categories.Add);
            existing.Tags.Clear();
            tags.Split(",", StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim().ToLowerInvariant())
                .ToList()
                .ForEach(existing.Tags.Add);
            existing.Title = post.Title.Trim();
            existing.Slug = !string.IsNullOrWhiteSpace(post.Slug) ? post.Slug.Trim() : Models.Post.CreateSlug(post.Title);
            existing.IsPublished = post.IsPublished;
            existing.Content = post.Content.Trim();
            existing.Excerpt = post.Excerpt.Trim();

            await this.SaveFilesToDisk(existing).ConfigureAwait(false);

            await this.blog.SavePost(existing).ConfigureAwait(false);

            return RedirectToAction(nameof(Post), new { slug = post.Slug });
        }

        private async Task SaveFilesToDisk(Post post)
        {
            var imgRegex = new Regex("<img[^>]+ />", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var base64Regex = new Regex("data:[^/]+/(?<ext>[a-z]+);base64,(?<base64>.+)", RegexOptions.IgnoreCase);
            var allowedExtensions = new[] {
              ".jpg",
              ".jpeg",
              ".gif",
              ".png",
              ".webp"
            };

            foreach (Match? match in imgRegex.Matches(post.Content))
            {
                if (match is null)
                {
                    continue;
                }

                var doc = new XmlDocument();
                doc.LoadXml($"<root>{match.Value}</root>");

                var img = doc.FirstChild.FirstChild;
                var srcNode = img.Attributes["src"];
                var fileNameNode = img.Attributes["data-filename"];

                if (srcNode is null)
                {
                    continue;
                }

                // The HTML editor creates base64 DataURIs which we'll have to convert to image files on disk
                var base64Match = base64Regex.Match(srcNode.Value);
                if (!base64Match.Success)
                {
                    continue;
                }

                string extension;
                string fileName;
                if (fileNameNode is null)
                {
                    // if an image has been pasted into the editor, it has no data-filename attribute!
                    extension = "." + base64Match.Groups["ext"].Value;
                    fileName = "pastedImage" + extension;
                }
                else
                {
                    extension = System.IO.Path.GetExtension(fileNameNode.Value);
                    fileName = fileNameNode.Value;
                }                

                // Only accept image files
                if (!allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                
                var bytes = Convert.FromBase64String(base64Match.Groups["base64"].Value);
                srcNode.Value = await this.blog.SaveFile(bytes, fileName).ConfigureAwait(false);

                img.Attributes.Remove(fileNameNode);
                post.Content = post.Content.Replace(match.Value, img.OuterXml, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
