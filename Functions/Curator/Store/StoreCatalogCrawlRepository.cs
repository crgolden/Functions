namespace Functions.Curator.Store;

using System.Data.Common;
using System.Text.Json;
using Functions.Curator.Catalog;
using Functions.Extensions;

public sealed class StoreCatalogCrawlRepository
{
    internal const string CrawlLockKey = "store_catalog_crawl";

    private readonly DbDataSource _dataSource;

    public StoreCatalogCrawlRepository(DbDataSource dataSource) => _dataSource = dataSource;

    public Task<AdvisoryLockHandle> TryLockCrawlAsync(CancellationToken cancellationToken = default) =>
        AdvisoryLockHandle.TryAcquireAsync(
            _dataSource, CuratorAdvisoryLocks.StoreCatalogCrawl, CrawlLockKey, cancellationToken);

    public async Task<List<StoreCrawlCategory>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT category_id, platform, reporting_name_prefix, next_offset, walk_completed_at
            FROM store_crawl_categories
            ORDER BY platform
            """;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var categories = new List<StoreCrawlCategory>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var completedAt = await reader.IsDBNullAsync(4, cancellationToken)
                ? (DateTimeOffset?)null
                : reader.GetFieldValue<DateTimeOffset>(4);
            categories.Add(new StoreCrawlCategory(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                completedAt));
        }

        return categories;
    }

    public async Task SaveProgressAsync(
        Guid categoryId,
        int nextOffset,
        int reportedTotal,
        bool completed,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE store_crawl_categories
            SET next_offset = @next_offset,
                reported_total = @reported_total,
                walk_started_at = COALESCE(walk_started_at, now()),
                walk_completed_at = CASE WHEN @completed THEN now() ELSE walk_completed_at END,
                updated_at = now()
            WHERE category_id = @category_id
            """;
        cmd.AddParam(CuratorSqlParameters.NextOffset, nextOffset);
        cmd.AddParam(CuratorSqlParameters.ReportedTotal, reportedTotal);
        cmd.AddParam(CuratorSqlParameters.Completed, completed);
        cmd.AddParam(CuratorSqlParameters.CategoryId, categoryId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RestartWalkAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE store_crawl_categories
            SET next_offset = 0, walk_started_at = now(), walk_completed_at = NULL, updated_at = now()
            WHERE category_id = @category_id
            """;
        cmd.AddParam(CuratorSqlParameters.CategoryId, categoryId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> AdmitAsync(
        IReadOnlyList<StoreCategoryProduct> products,
        CancellationToken cancellationToken = default)
    {
        var created = 0;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        foreach (var product in products)
        {
            var canonicalTitle = CanonicalizationService.NormalizeName(product.Name);
            if (canonicalTitle is null)
            {
                continue;
            }

            if (await AdmitOneAsync(connection, product, canonicalTitle, cancellationToken))
            {
                created++;
            }
        }

        return created;
    }

    private static async Task<bool> AdmitOneAsync(
        DbConnection connection,
        StoreCategoryProduct product,
        string canonicalTitle,
        CancellationToken cancellationToken)
    {
        var normalizedTitle = canonicalTitle.Trim().ToLowerInvariant();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await AdvisoryLockHandle.HoldUntilCommitAsync(
            connection, transaction, CuratorAdvisoryLocks.GameUpsert, normalizedTitle, cancellationToken);

        await using var byTitle = connection.CreateCommand();
        byTitle.Transaction = transaction;
        byTitle.CommandText = "SELECT game_id FROM games WHERE normalized_title = @normalized_title";
        byTitle.AddParam(CuratorSqlParameters.NormalizedTitle, normalizedTitle);
        var existingGameId = (Guid?)await byTitle.ExecuteScalarAsync(cancellationToken);

        Guid gameId;
        var created = false;
        if (existingGameId is { } resolvedGameId)
        {
            gameId = resolvedGameId;
        }
        else
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO games (canonical_title, normalized_title, content_kind, store_cover_image_url)
                VALUES (@canonical_title, @normalized_title, @content_kind, @cover_image_url)
                RETURNING game_id
                """;
            insert.AddParam(CuratorSqlParameters.CanonicalTitle, canonicalTitle);
            insert.AddParam(CuratorSqlParameters.NormalizedTitle, normalizedTitle);
            insert.AddParam(CuratorSqlParameters.ContentKind, ContentKind.Game.ToWireName());
            insert.AddParam(CuratorSqlParameters.CoverImageUrl, product.CoverImageUrl);
            gameId = (Guid?)await insert.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Inserting a game returned no game_id.");
            created = true;
        }

        if (product.CoverImageUrl is not null)
        {
            await using var fillCover = connection.CreateCommand();
            fillCover.Transaction = transaction;
            fillCover.CommandText = """
                UPDATE games SET store_cover_image_url = @cover_image_url
                WHERE game_id = @game_id AND store_cover_image_url IS NULL
                """;
            fillCover.AddParam(CuratorSqlParameters.CoverImageUrl, product.CoverImageUrl);
            fillCover.AddParam(CuratorSqlParameters.GameId, gameId);
            await fillCover.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var enrichment = connection.CreateCommand();
        enrichment.Transaction = transaction;
        enrichment.CommandText =
            "INSERT INTO game_enrichment (game_id) VALUES (@game_id) ON CONFLICT (game_id) DO NOTHING";
        enrichment.AddParam(CuratorSqlParameters.GameId, gameId);
        await enrichment.ExecuteNonQueryAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(product.NpTitleId))
        {
            await using var cache = connection.CreateCommand();
            cache.Transaction = transaction;
            cache.CommandText = """
                INSERT INTO psn_catalog_cache
                    (title_id, game_id, store_product_id, cover_image_url, raw, fetched_at,
                     price_is_free, price_tied_to_subscription, price_base_cents,
                     price_discounted_cents, price_discount_text, price_fetched_at)
                VALUES (@title_id, @game_id, @store_product_id, @cover_image_url, @raw::jsonb, now(),
                        @price_is_free, @price_tied_to_subscription, @price_base_cents,
                        @price_discounted_cents, @price_discount_text,
                        CASE WHEN @price_present THEN now() END)
                ON CONFLICT (title_id) DO UPDATE SET
                    game_id = EXCLUDED.game_id,
                    store_product_id = EXCLUDED.store_product_id,
                    cover_image_url = COALESCE(EXCLUDED.cover_image_url, psn_catalog_cache.cover_image_url),
                    raw = CASE WHEN EXCLUDED.raw = '{}'::jsonb THEN psn_catalog_cache.raw ELSE EXCLUDED.raw END,
                    fetched_at = now(),
                    price_is_free = COALESCE(EXCLUDED.price_is_free, psn_catalog_cache.price_is_free),
                    price_tied_to_subscription = COALESCE(
                        EXCLUDED.price_tied_to_subscription, psn_catalog_cache.price_tied_to_subscription
                    ),
                    price_base_cents = CASE WHEN EXCLUDED.price_fetched_at IS NOT NULL
                        THEN EXCLUDED.price_base_cents ELSE psn_catalog_cache.price_base_cents END,
                    price_discounted_cents = CASE WHEN EXCLUDED.price_fetched_at IS NOT NULL
                        THEN EXCLUDED.price_discounted_cents ELSE psn_catalog_cache.price_discounted_cents END,
                    price_discount_text = CASE WHEN EXCLUDED.price_fetched_at IS NOT NULL
                        THEN EXCLUDED.price_discount_text ELSE psn_catalog_cache.price_discount_text END,
                    price_fetched_at = COALESCE(EXCLUDED.price_fetched_at, psn_catalog_cache.price_fetched_at)
                """;
            cache.AddParam(CuratorSqlParameters.TitleId, product.NpTitleId);
            cache.AddParam(CuratorSqlParameters.GameId, gameId);
            cache.AddParam(CuratorSqlParameters.StoreProductId, product.Id);
            cache.AddParam(CuratorSqlParameters.CoverImageUrl, product.CoverImageUrl);
            cache.AddParam(CuratorSqlParameters.Raw, JsonSerializer.Serialize(product));
            cache.AddParam(CuratorSqlParameters.PriceIsFree, product.Price?.IsFree);
            cache.AddParam(CuratorSqlParameters.PriceTiedToSubscription, product.Price?.IsTiedToSubscription);
            cache.AddParam(CuratorSqlParameters.PriceBaseCents, product.Price?.BaseCents);
            cache.AddParam(CuratorSqlParameters.PriceDiscountedCents, product.Price?.DiscountedCents);
            cache.AddParam(CuratorSqlParameters.PriceDiscountText, product.Price?.DiscountText);
            cache.AddParam(CuratorSqlParameters.PricePresent, product.Price is not null);
            await cache.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return created;
    }
}
