package main

import (
	"compress/gzip"
	"database/sql"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
	"math"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"slices"
	"sort"
	"strings"
	"time"

	"github.com/thomersch/gosmparse"
	_ "modernc.org/sqlite"
)

// ---------------------------------------------------------------------------
// Geofabrik index
// ---------------------------------------------------------------------------

type GeofabrikIndex struct {
	Features []GeofabrikFeature `json:"features"`
}

type GeofabrikFeature struct {
	Properties GeofabrikProps `json:"properties"`
}

type GeofabrikProps struct {
	ID     string            `json:"id"`
	Name   string            `json:"name"`
	Parent string            `json:"parent"`
	URLs   map[string]string `json:"urls"`
}

type Region struct {
	ID       string
	Name     string
	Parent   string
	PbfURL   string
	Children []*Region
}

func fetchIndex() (map[string]*Region, error) {
	fmt.Println("Fetching Geofabrik index...")
	resp, err := http.Get("https://download.geofabrik.de/index-v1-nogeom.json")
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()

	var idx GeofabrikIndex
	if err := json.NewDecoder(resp.Body).Decode(&idx); err != nil {
		return nil, err
	}

	regions := make(map[string]*Region)
	for _, f := range idx.Features {
		r := &Region{
			ID:     f.Properties.ID,
			Name:   f.Properties.Name,
			Parent: f.Properties.Parent,
			PbfURL: f.Properties.URLs["pbf"],
		}
		regions[r.ID] = r
	}

	for _, r := range regions {
		if r.Parent != "" {
			if parent, ok := regions[r.Parent]; ok {
				parent.Children = append(parent.Children, r)
			}
		}
	}

	for _, r := range regions {
		sort.Slice(r.Children, func(i, j int) bool {
			return r.Children[i].Name < r.Children[j].Name
		})
	}

	return regions, nil
}

func getRoots(regions map[string]*Region) []*Region {
	var roots []*Region
	for _, r := range regions {
		if r.Parent == "" {
			roots = append(roots, r)
		}
	}
	sort.Slice(roots, func(i, j int) bool {
		return roots[i].Name < roots[j].Name
	})
	return roots
}

func selectRegion(regions map[string]*Region) *Region {
	current := getRoots(regions)
	var breadcrumb []string

	for {
		fmt.Println()
		if len(breadcrumb) > 0 {
			fmt.Printf("  %s\n", strings.Join(breadcrumb, " > "))
		}
		fmt.Println()

		for i, r := range current {
			suffix := ""
			if len(r.Children) > 0 {
				suffix = " >"
			}
			fmt.Printf("  [%2d] %s%s\n", i+1, r.Name, suffix)
		}

		if len(breadcrumb) > 0 {
			parentID := current[0].Parent
			if parent, ok := regions[parentID]; ok && parent.PbfURL != "" {
				fmt.Printf("\n  [  0] Download ALL of %s\n", parent.Name)
			}
		}

		fmt.Print("\nSelect: ")
		var choice int
		if _, err := fmt.Scan(&choice); err != nil {
			continue
		}

		if choice == 0 && len(breadcrumb) > 0 {
			parentID := current[0].Parent
			if parent, ok := regions[parentID]; ok {
				return parent
			}
			continue
		}

		if choice < 1 || choice > len(current) {
			fmt.Println("Invalid choice.")
			continue
		}

		selected := current[choice-1]

		if len(selected.Children) == 0 {
			return selected
		}

		breadcrumb = append(breadcrumb, selected.Name)
		current = selected.Children
	}
}

// ---------------------------------------------------------------------------
// PBF download
// ---------------------------------------------------------------------------

func downloadPbf(url, destPath string) error {
	fmt.Printf("Downloading %s ...\n", url)

	resp, err := http.Get(url)
	if err != nil {
		return err
	}
	defer resp.Body.Close()

	if resp.StatusCode != 200 {
		return fmt.Errorf("HTTP %d", resp.StatusCode)
	}

	totalSize := resp.ContentLength

	f, err := os.Create(destPath)
	if err != nil {
		return err
	}
	defer f.Close()

	var written int64
	buf := make([]byte, 1024*1024)
	lastReport := time.Now()

	for {
		n, readErr := resp.Body.Read(buf)
		if n > 0 {
			if _, werr := f.Write(buf[:n]); werr != nil {
				return werr
			}
			written += int64(n)

			if time.Since(lastReport) > time.Second {
				if totalSize > 0 {
					fmt.Printf("\r  %.1f / %.1f MB (%.0f%%)  ",
						float64(written)/1048576,
						float64(totalSize)/1048576,
						float64(written)/float64(totalSize)*100)
				} else {
					fmt.Printf("\r  %.1f MB  ", float64(written)/1048576)
				}
				lastReport = time.Now()
			}
		}
		if readErr == io.EOF {
			break
		}
		if readErr != nil {
			return readErr
		}
	}

	fmt.Printf("\r  %.1f MB — done               \n", float64(written)/1048576)
	return nil
}

// ---------------------------------------------------------------------------
// OSM classification (matches C# OsmPbfParser exactly)
// ---------------------------------------------------------------------------

const (
	TypeRoad  = 0
	TypeWater = 1
	TypePark  = 2
)

var roadKeys = map[string]bool{
	"motorway": true, "trunk": true, "primary": true, "secondary": true, "tertiary": true,
	"motorway_link": true, "trunk_link": true, "primary_link": true, "secondary_link": true, "tertiary_link": true,
	"residential": true, "unclassified": true, "service": true, "living_street": true, "pedestrian": true,
	"track": true, "footway": true, "cycleway": true, "path": true,
}

var poiKeys = map[string]bool{
	"amenity": true, "shop": true, "tourism": true, "healthcare": true, "office": true,
}

func classifyWay(tags map[string]string) (wayType int, subType string, ok bool) {
	for k, v := range tags {
		switch k {
		case "highway":
			if roadKeys[v] {
				return TypeRoad, v, true
			}
		case "waterway":
			return TypeWater, v, true
		case "natural":
			switch v {
			case "water":
				return TypeWater, v, true
			case "wood", "scrub":
				return TypePark, v, true
			}
		case "water":
			return TypeWater, v, true
		case "leisure":
			switch v {
			case "park", "garden", "nature_reserve":
				return TypePark, v, true
			}
		case "landuse":
			switch v {
			case "forest", "grass", "meadow", "farmland", "orchard", "vineyard", "recreation_ground":
				return TypePark, v, true
			}
		}
	}
	return 0, "", false
}

func classifyPoi(tags map[string]string) (poiType, poiSubType, name string, ok bool) {
	for k, v := range tags {
		if poiKeys[k] {
			poiType = k
			poiSubType = v
		}
		if k == "name" {
			name = v
		}
	}
	if poiType != "" {
		return poiType, poiSubType, name, true
	}
	return "", "", "", false
}

// ---------------------------------------------------------------------------
// Geometry encoding (matches C# MapDatabase.EncodeGeometry)
// Packed little-endian doubles: [lat0, lon0, lat1, lon1, ...]
// ---------------------------------------------------------------------------

func encodeGeometry(lats, lons []float64) []byte {
	buf := make([]byte, len(lats)*16)
	for i := range lats {
		binary.LittleEndian.PutUint64(buf[i*16:], math.Float64bits(lats[i]))
		binary.LittleEndian.PutUint64(buf[i*16+8:], math.Float64bits(lons[i]))
	}
	return buf
}

// ---------------------------------------------------------------------------
// Database setup (matches C# MapDatabase schema exactly)
// ---------------------------------------------------------------------------

func createDB(dbPath string) (*sql.DB, error) {
	db, err := sql.Open("sqlite", dbPath)
	if err != nil {
		return nil, err
	}

	pragmas := []string{
		"PRAGMA journal_mode=WAL",
		"PRAGMA synchronous=OFF",
		"PRAGMA cache_size=-32000",
		"PRAGMA temp_store=MEMORY",
		"PRAGMA page_size=4096",
		"PRAGMA mmap_size=67108864",
	}
	for _, p := range pragmas {
		if _, err := db.Exec(p); err != nil {
			return nil, fmt.Errorf("%s: %w", p, err)
		}
	}

	schema := []string{
		`CREATE TABLE IF NOT EXISTS ways (
			id INTEGER PRIMARY KEY,
			type INTEGER NOT NULL,
			subtype TEXT,
			geometry BLOB NOT NULL,
			min_lat REAL NOT NULL,
			max_lat REAL NOT NULL,
			min_lon REAL NOT NULL,
			max_lon REAL NOT NULL
		)`,
		`CREATE TABLE IF NOT EXISTS metadata (
			key TEXT PRIMARY KEY,
			value TEXT
		)`,
		`CREATE TABLE IF NOT EXISTS pois (
			id INTEGER PRIMARY KEY,
			type TEXT NOT NULL,
			subtype TEXT,
			name TEXT,
			lat REAL NOT NULL,
			lon REAL NOT NULL
		)`,
	}
	for _, s := range schema {
		if _, err := db.Exec(s); err != nil {
			return nil, err
		}
	}

	return db, nil
}

func createIndexes(db *sql.DB) {
	// PRAGMA threads lets SQLite use helper threads for sorting during index creation.
	db.Exec("PRAGMA threads=4")
	db.Exec("CREATE INDEX IF NOT EXISTS idx_ways_bounds ON ways(min_lat, max_lat, min_lon, max_lon)")
	db.Exec("CREATE INDEX IF NOT EXISTS idx_ways_type_bounds ON ways(type, min_lat, max_lat, min_lon, max_lon)")
	db.Exec("CREATE INDEX IF NOT EXISTS idx_pois_coords ON pois(lat, lon)")
}

// ---------------------------------------------------------------------------
// gosmparse handlers for two-pass import (pure Go, no CGO)
// ---------------------------------------------------------------------------

type pass1Handler struct {
	nodeIDs  []int64
	scanWays int64
}

func (h *pass1Handler) ReadNode(n gosmparse.Node)         {}
func (h *pass1Handler) ReadRelation(r gosmparse.Relation) {}
func (h *pass1Handler) ReadWay(w gosmparse.Way) {
	if _, _, ok := classifyWay(w.Tags); !ok {
		return
	}
	for _, nid := range w.NodeIDs {
		h.nodeIDs = append(h.nodeIDs, nid)
	}
	h.scanWays++
}

const (
	wayInsertSQL = `INSERT OR IGNORE INTO ways(id, type, subtype, geometry, min_lat, max_lat, min_lon, max_lon) VALUES(?, ?, ?, ?, ?, ?, ?, ?)`
	poiInsertSQL = `INSERT OR IGNORE INTO pois(id, type, subtype, name, lat, lon) VALUES(?, ?, ?, ?, ?, ?)`
)

type pass2Handler struct {
	nodeIDs    []int64
	latBuf     []float64
	lonBuf     []float64
	db         *sql.DB
	tx         *sql.Tx
	wayStmt    *sql.Stmt
	poiStmt    *sql.Stmt
	nodeCount  int64
	wayCount   int64
	poiCount   int64
	batchCount int64
	lastReport time.Time
}

func (h *pass2Handler) ReadRelation(r gosmparse.Relation) {}

func (h *pass2Handler) ReadNode(n gosmparse.Node) {
	h.nodeCount++
	if idx, found := slices.BinarySearch(h.nodeIDs, n.ID); found {
		h.latBuf[idx] = n.Lat
		h.lonBuf[idx] = n.Lon
	}
	if len(n.Tags) > 0 {
		if pt, ps, pn, ok := classifyPoi(n.Tags); ok {
			h.poiStmt.Exec(n.ID, pt, ps, nullStr(pn), n.Lat, n.Lon)
			h.poiCount++
			h.batchCount++
			h.maybeFlush()
		}
	}
	if time.Since(h.lastReport) > 2*time.Second {
		fmt.Printf("\r  Nodes: %dk | Ways: %dk | POIs: %dk  ",
			h.nodeCount/1000, h.wayCount/1000, h.poiCount/1000)
		h.lastReport = time.Now()
	}
}

func (h *pass2Handler) ReadWay(w gosmparse.Way) {
	wt, ws, ok := classifyWay(w.Tags)
	if !ok {
		return
	}
	lats := make([]float64, 0, len(w.NodeIDs))
	lons := make([]float64, 0, len(w.NodeIDs))
	minLat, maxLat := math.MaxFloat64, -math.MaxFloat64
	minLon, maxLon := math.MaxFloat64, -math.MaxFloat64
	for _, nid := range w.NodeIDs {
		if idx, found := slices.BinarySearch(h.nodeIDs, nid); found {
			lat := h.latBuf[idx]
			lon := h.lonBuf[idx]
			lats = append(lats, lat)
			lons = append(lons, lon)
			if lat < minLat {
				minLat = lat
			}
			if lat > maxLat {
				maxLat = lat
			}
			if lon < minLon {
				minLon = lon
			}
			if lon > maxLon {
				maxLon = lon
			}
		}
	}
	if len(lats) < 2 {
		return
	}
	geo := encodeGeometry(lats, lons)
	h.wayStmt.Exec(w.ID, wt, ws, geo, minLat, maxLat, minLon, maxLon)
	h.wayCount++
	h.batchCount++
	h.maybeFlush()
}

func (h *pass2Handler) maybeFlush() {
	if h.batchCount < 500_000 {
		return
	}
	h.wayStmt.Close()
	h.poiStmt.Close()
	h.tx.Commit()
	h.tx, _ = h.db.Begin()
	h.wayStmt, _ = h.tx.Prepare(wayInsertSQL)
	h.poiStmt, _ = h.tx.Prepare(poiInsertSQL)
	h.batchCount = 0
}

// ---------------------------------------------------------------------------
// Two-pass PBF import
// ---------------------------------------------------------------------------

func importPbf(pbfPath, dbPath string) error {
	totalStart := time.Now()

	db, err := createDB(dbPath)
	if err != nil {
		return err
	}
	defer db.Close()

	pbfStat, _ := os.Stat(pbfPath)
	pbfSize := pbfStat.Size()

	// --- Pass 1: scan ways, collect referenced node IDs ---
	fmt.Println("\nPass 1: Scanning ways for node references...")
	pass1Start := time.Now()

	f1, err := os.Open(pbfPath)
	if err != nil {
		return err
	}
	h1 := &pass1Handler{nodeIDs: make([]int64, 0, 200_000_000)}
	if err := gosmparse.NewDecoder(f1).Parse(h1); err != nil {
		f1.Close()
		return err
	}
	f1.Close()
	nodeIDs := h1.nodeIDs
	scanWays := h1.scanWays

	fmt.Printf("  Sorting %d raw node refs...\n", len(nodeIDs))
	slices.Sort(nodeIDs)
	nodeIDs = slices.Compact(nodeIDs)

	pass1Dur := time.Since(pass1Start)
	fmt.Printf("  %d ways → %d unique node refs (%s)\n", scanWays, len(nodeIDs), pass1Dur.Round(time.Millisecond))

	// Parallel float64 arrays — no pointer heap, GC scans them in O(1)
	latBuf := make([]float64, len(nodeIDs))
	lonBuf := make([]float64, len(nodeIDs))

	// --- Pass 2: fill coordinate arrays, resolve ways, extract POIs ---
	fmt.Println("\nPass 2: Importing...")
	pass2Start := time.Now()

	f2, err := os.Open(pbfPath)
	if err != nil {
		return err
	}
	defer f2.Close()

	tx, err := db.Begin()
	if err != nil {
		return err
	}
	wayStmt, err := tx.Prepare(wayInsertSQL)
	if err != nil {
		tx.Rollback()
		return err
	}
	poiStmt, err := tx.Prepare(poiInsertSQL)
	if err != nil {
		tx.Rollback()
		return err
	}

	h2 := &pass2Handler{
		nodeIDs:    nodeIDs,
		latBuf:     latBuf,
		lonBuf:     lonBuf,
		db:         db,
		tx:         tx,
		wayStmt:    wayStmt,
		poiStmt:    poiStmt,
		lastReport: time.Now(),
	}

	if err := gosmparse.NewDecoder(f2).Parse(h2); err != nil {
		h2.wayStmt.Close()
		h2.poiStmt.Close()
		h2.tx.Rollback()
		return err
	}
	h2.wayStmt.Close()
	h2.poiStmt.Close()
	h2.tx.Commit()

	pass2Dur := time.Since(pass2Start)
	fmt.Printf("\r  Nodes: %dk | Ways: %dk | POIs: %dk (%s)\n",
		h2.nodeCount/1000, h2.wayCount/1000, h2.poiCount/1000, pass2Dur.Round(time.Millisecond))

	// Free memory before indexing
	nodeIDs = nil
	latBuf = nil
	lonBuf = nil
	runtime.GC()

	indexStart := time.Now()
	fmt.Print("Building indexes...")
	createIndexes(db)
	indexDur := time.Since(indexStart)
	fmt.Printf(" done (%s)\n", indexDur.Round(time.Millisecond))

	db.Exec("INSERT OR REPLACE INTO metadata(key, value) VALUES(?, ?)",
		"import_date", time.Now().UTC().Format(time.RFC3339))
	db.Exec("INSERT OR REPLACE INTO metadata(key, value) VALUES(?, ?)",
		"source", filepath.Base(pbfPath))

	totalDur := time.Since(totalStart)
	dbStat, _ := os.Stat(dbPath)

	fmt.Println()
	fmt.Println("=== Import Summary ===")
	fmt.Printf("  PBF size:    %.1f MB\n", float64(pbfSize)/1048576)
	fmt.Printf("  DB size:     %.1f MB\n", float64(dbStat.Size())/1048576)
	fmt.Printf("  Ways:        %d\n", h2.wayCount)
	fmt.Printf("  POIs:        %d\n", h2.poiCount)
	fmt.Printf("  Nodes read:  %d\n", h2.nodeCount)
	fmt.Printf("  Pass 1:      %s\n", pass1Dur.Round(time.Millisecond))
	fmt.Printf("  Pass 2:      %s\n", pass2Dur.Round(time.Millisecond))
	fmt.Printf("  Indexing:    %s\n", indexDur.Round(time.Millisecond))
	fmt.Printf("  Total:       %s\n", totalDur.Round(time.Millisecond))

	return nil
}

func nullStr(s string) interface{} {
	if s == "" {
		return nil
	}
	return s
}

// ---------------------------------------------------------------------------
// Compress the database with gzip
// ---------------------------------------------------------------------------

func compressDB(dbPath, gzPath string) error {
	fmt.Print("Compressing database... ")

	in, err := os.Open(dbPath)
	if err != nil {
		return err
	}
	defer in.Close()

	inStat, _ := in.Stat()
	inSize := inStat.Size()

	out, err := os.Create(gzPath)
	if err != nil {
		return err
	}
	defer out.Close()

	gz, err := gzip.NewWriterLevel(out, gzip.DefaultCompression)
	if err != nil {
		return err
	}

	if _, err := io.Copy(gz, in); err != nil {
		gz.Close()
		return err
	}
	gz.Close()

	outStat, _ := os.Stat(gzPath)
	fmt.Printf("%.1f MB -> %.1f MB (%.0f%% of original)\n",
		float64(inSize)/1048576,
		float64(outStat.Size())/1048576,
		float64(outStat.Size())/float64(inSize)*100)

	return nil
}

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------

func main() {
	fmt.Println("=== WinMaps DB Prebuild Tool ===")
	fmt.Printf("Runtime: %d CPUs, GOMAXPROCS=%d\n", runtime.NumCPU(), runtime.GOMAXPROCS(0))
	fmt.Println()

	regions, err := fetchIndex()
	if err != nil {
		fmt.Fprintf(os.Stderr, "Failed to fetch index: %v\n", err)
		os.Exit(1)
	}
	fmt.Printf("Loaded %d regions\n", len(regions))

	selected := selectRegion(regions)
	if selected.PbfURL == "" {
		fmt.Fprintf(os.Stderr, "No PBF download available for %s\n", selected.Name)
		os.Exit(1)
	}

	fmt.Printf("\nSelected: %s\n", selected.Name)
	fmt.Printf("PBF URL:  %s\n", selected.PbfURL)

	// Output paths
	safeID := strings.ReplaceAll(selected.ID, "/", "_")
	outDir := "output"
	os.MkdirAll(outDir, 0755)

	pbfPath := filepath.Join(outDir, safeID+".osm.pbf")
	dbPath := filepath.Join(outDir, safeID+".osm.db")
	gzPath := filepath.Join(outDir, safeID+".osm.db.gz")

	// Download PBF (skip if already cached)
	if info, err := os.Stat(pbfPath); err == nil {
		fmt.Printf("Using cached PBF (%.1f MB)\n", float64(info.Size())/1048576)
	} else {
		if err := downloadPbf(selected.PbfURL, pbfPath); err != nil {
			fmt.Fprintf(os.Stderr, "Download failed: %v\n", err)
			os.Exit(1)
		}
	}

	// Clean up old DB files
	os.Remove(dbPath)
	os.Remove(dbPath + "-wal")
	os.Remove(dbPath + "-shm")

	// Import
	if err := importPbf(pbfPath, dbPath); err != nil {
		fmt.Fprintf(os.Stderr, "\nImport failed: %v\n", err)
		os.Exit(1)
	}

	// Compress
	if err := compressDB(dbPath, gzPath); err != nil {
		fmt.Fprintf(os.Stderr, "Compression failed: %v\n", err)
	}

	fmt.Println("\nDone!")
	fmt.Printf("  DB:  %s\n", dbPath)
	fmt.Printf("  GZ:  %s\n", gzPath)
}
