using Microsoft.EntityFrameworkCore;
using QuotesApi.Models;

namespace QuotesApi.Data;

public static class DbInitializer
{
    public static async Task SeedAsync(QuotesDbContext db)
    {
        // Seed once on a fresh database with meaningful quotes only.
        if (!await db.Quotes.AnyAsync())
        {
            var seedQuotes = new[]
            {
                Quote.Create("Steve Jobs", "The only way to do great work is to love what you do."),
                Quote.Create("John Lennon", "Life is what happens when you're busy making other plans."),
                Quote.Create("Eleanor Roosevelt", "The future belongs to those who believe in the beauty of their dreams."),
                Quote.Create("Nelson Mandela", "It always seems impossible until it's done."),
                Quote.Create("Albert Einstein", "In the middle of difficulty lies opportunity."),
                Quote.Create("Winston Churchill", "Success is not final, failure is not fatal: it is the courage to continue that counts."),
                Quote.Create("Ralph Waldo Emerson", "Do not go where the path may lead, go instead where there is no path and leave a trail."),
                Quote.Create("Wayne Gretzky", "You miss 100% of the shots you don't take."),
                Quote.Create("Theodore Roosevelt", "Believe you can and you're halfway there."),
                Quote.Create("Chinese Proverb", "The best time to plant a tree was 20 years ago. The second best time is now."),
                Quote.Create("Lao Tzu", "The journey of a thousand miles begins with one step."),
                Quote.Create("Zig Ziglar", "What you get by achieving your goals is not as important as what you become by achieving your goals."),
                Quote.Create("Oprah Winfrey", "Turn your wounds into wisdom."),
                Quote.Create("Walt Whitman", "Keep your face always toward the sunshine—and shadows will fall behind you."),
                Quote.Create("Aristotle", "Happiness depends upon ourselves."),
                Quote.Create("George Addair", "Everything you've ever wanted is on the other side of fear."),
                Quote.Create("William James", "Act as if what you do makes a difference. It does."),
                Quote.Create("Maya Angelou", "You will face many defeats in life, but never let yourself be defeated."),
                Quote.Create("Oscar Wilde", "Be yourself; everyone else is already taken."),
                Quote.Create("Mahatma Gandhi", "Be the change that you wish to see in the world.")
            };

            db.Quotes.AddRange(seedQuotes);
            await db.SaveChangesAsync();
        }
    }
}
