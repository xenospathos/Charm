import unreal
import os
import json


class CharmImporter:
    def __init__(self, folder_path: str, b_unique_folder: bool) -> None:
        self.folder_path = folder_path
        script_name = os.path.basename(__file__)
        self.base_hash = script_name.split('_')[0]
        info_name = f"{self.base_hash}_info.cfg"
        self.config = json.load(open(os.path.join(self.folder_path, info_name)))
        # Stashed reference to the scene's DirectionalLight (set in ensure_map when created),
        # so import_atmosphere can retarget it without an O(N) get_all_level_actors() walk.
        self._sun_actor = None
        if b_unique_folder:
            self.content_path = f"{self.config['UnrealInteropPath']}/{self.config['MeshName']}"
        else:
            self.content_path = f"{self.config['UnrealInteropPath']}"
        if not unreal.EditorAssetLibrary.does_directory_exist(self.content_path):
            unreal.EditorAssetLibrary.make_directory(self.content_path)

        # Load additional configs for terrain, entities, decorators, sky objects
        self.extra_configs = {}
        for suffix in ("Terrain", "Entities", "Decorators", "SkyObjects", "RoadDecals", "SpeedTrees", "WaterDecals"):
            cfg_path = os.path.join(self.folder_path, f"{self.base_hash}_{suffix}_info.cfg")
            if os.path.exists(cfg_path):
                self.extra_configs[suffix] = json.load(open(cfg_path))

    def import_entity(self):
        self.make_materials()
        self.import_entity_mesh()
        self.assign_entity_materials()
        unreal.EditorAssetLibrary.save_directory(f"/Game/{self.content_path}/", False)

    def import_static(self):
        self.make_materials()
        self.import_static_mesh(combine=True)
        self.assign_static_materials()
        unreal.EditorAssetLibrary.save_directory(f"/Game/{self.content_path}/", False)

    def import_map(self):
        # Ensure map level exists before importing any assets
        self.ensure_map()

        # Statics (may be empty for terrain/decorator-only hashes)
        if self.config.get("Parts") or self.config.get("Instances"):
            self.make_materials()
            self.import_map_statics()
            self.assign_map_materials()

        # Terrain
        if "Terrain" in self.extra_configs:
            self.make_materials(self.extra_configs["Terrain"])
            self.import_map_fbx_dir("Terrain")
            self.assign_type_materials("Terrain")

        # Entities
        if "Entities" in self.extra_configs:
            self.make_materials(self.extra_configs["Entities"])
            self.import_map_fbx_dir("Entities")
            self.assign_type_materials("Entities")

        # Decorators
        if "Decorators" in self.extra_configs:
            self.make_materials(self.extra_configs["Decorators"])
            self.import_map_fbx_dir("Decorators")
            self.assign_type_materials("Decorators")

        # Sky Objects
        if "SkyObjects" in self.extra_configs:
            self.make_materials(self.extra_configs["SkyObjects"])
            self.import_map_fbx_dir("SkyObjects")
            self.assign_type_materials("SkyObjects")

        # Road Decals (mesh-based projected geometry)
        if self.config.get("GenerateDecals", False) and "RoadDecals" in self.extra_configs:
            self.make_materials(self.extra_configs["RoadDecals"])
            self.import_map_fbx_dir("RoadDecals")
            self.assign_type_materials("RoadDecals")

        # SpeedTrees (instanced tree foliage from decorators)
        if self.config.get("GenerateSpeedTrees", False) and "SpeedTrees" in self.extra_configs:
            self.make_materials(self.extra_configs["SpeedTrees"])
            self.import_map_fbx_dir("SpeedTrees")
            self.assign_type_materials("SpeedTrees")

        # Water Decals (screen-space reflected water planes)
        if self.config.get("GenerateDecals", False) and "WaterDecals" in self.extra_configs:
            self.make_materials(self.extra_configs["WaterDecals"])
            self.import_map_fbx_dir("WaterDecals")
            self.assign_type_materials("WaterDecals")

        self.assemble_map()

        # Environment data — shared folder at map root (controlled by beta settings)
        if self.config.get("GenerateLights", False):
            self.import_lights()
            self.import_lens_flares()
        if self.config.get("GenerateSkybox", False):
            self.import_cubemaps()
        if self.config.get("GenerateAtmosphere", False):
            self.import_atmosphere()

        unreal.EditorAssetLibrary.save_directory(f"/Game/{self.content_path}/", False)

    def _set_complex_collision(self, asset_dir: str) -> None:
        """Set all static meshes in a directory to use complex collision as simple.

        Wrapped in a ScopedEditorTransaction so the editor coalesces post-mutation
        notifications (asset registry events, undo entries, viewport invalidations)
        into a single batch instead of firing once per mesh.
        """
        if not unreal.EditorAssetLibrary.does_directory_exist(asset_dir):
            return
        with unreal.ScopedEditorTransaction("Charm: Set complex collision") as _:
            for asset_path in unreal.EditorAssetLibrary.list_assets(asset_dir, recursive=False):
                mesh = unreal.EditorAssetLibrary.load_asset(asset_path)
                if mesh is None or not isinstance(mesh, unreal.StaticMesh):
                    continue
                body_setup = mesh.get_editor_property('body_setup')
                if body_setup is not None:
                    body_setup.set_editor_property('collision_trace_flag', unreal.CollisionTraceFlag.CTF_USE_COMPLEX_AS_SIMPLE)

    def import_map_statics(self) -> None:
        """Import all individual static FBX files from Models/Statics/ directory."""
        import glob
        statics_dir = os.path.join(self.folder_path, "Models", "Statics")
        if not os.path.exists(statics_dir):
            # Fallback: try old single-file import
            self.import_static_mesh(combine=False)
            return

        fbx_files = glob.glob(os.path.join(statics_dir, "*.fbx"))
        if not fbx_files:
            return


        tasks = []
        for fbx_path in fbx_files:
            task = self._make_static_import_task(fbx_path, f"/Game/{self.content_path}/Statics/")
            tasks.append(task)

        unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks(tasks)
        self._set_complex_collision(f"/Game/{self.content_path}/Statics/")

    def import_map_fbx_dir(self, type_name: str) -> None:
        """Import all FBX files from a Models/{type_name}/ subdirectory."""
        import glob
        model_dir = os.path.join(self.folder_path, "Models", type_name)
        if not os.path.exists(model_dir):
            return

        fbx_files = glob.glob(os.path.join(model_dir, "*.fbx"))
        if not fbx_files:
            return


        dest_path = f"/Game/{self.content_path}/{type_name}/"
        tasks = []
        for fbx_path in fbx_files:
            task = self._make_static_import_task(fbx_path, dest_path)
            tasks.append(task)

        unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks(tasks)
        self._set_complex_collision(dest_path)

    def _make_static_import_task(self, fbx_path: str, dest_path: str) -> unreal.AssetImportTask:
        """Create a static mesh import task."""
        task = unreal.AssetImportTask()
        task.set_editor_property("automated", True)
        task.set_editor_property("destination_path", dest_path)
        task.set_editor_property("filename", fbx_path)
        task.set_editor_property("replace_existing", True)
        task.set_editor_property("save", False)

        options = unreal.FbxImportUI()
        options.set_editor_property('import_mesh', True)
        options.set_editor_property('import_textures', False)
        options.set_editor_property('import_materials', False)
        options.set_editor_property('import_as_skeletal', False)
        options.static_mesh_import_data.set_editor_property('convert_scene', False)
        options.static_mesh_import_data.set_editor_property('import_uniform_scale', 100.0)
        options.static_mesh_import_data.set_editor_property('combine_meshes', True)
        options.static_mesh_import_data.set_editor_property('generate_lightmap_u_vs', False)
        options.static_mesh_import_data.set_editor_property('auto_generate_collision', False)
        options.static_mesh_import_data.set_editor_property('normal_import_method', unreal.FBXNormalImportMethod.FBXNIM_IMPORT_NORMALS)
        options.static_mesh_import_data.set_editor_property("vertex_color_import_option", unreal.VertexColorImportOption.REPLACE)
        options.static_mesh_import_data.set_editor_property("build_nanite", False)
        task.set_editor_property("options", options)
        return task

    def ensure_map(self) -> None:
        """Create the map level if it doesn't exist, or load it if it does."""
        map_path = f'/Game/{self.config["UnrealInteropPath"]}/map'
        if unreal.EditorAssetLibrary.does_asset_exist(map_path):
            unreal.EditorLevelLibrary.load_level(map_path)
        else:
            unreal.EditorLevelLibrary.new_level(map_path)
            # Add default scene actors into shared Environment folder
            if self.config.get("GenerateSkybox", False):
                sky = unreal.EditorLevelLibrary.spawn_actor_from_class(unreal.SkyLight, location=[0, 0, 10000])
                if sky:
                    sky.set_folder_path("Environment/Atmosphere")
                sky_atmo = unreal.EditorLevelLibrary.spawn_actor_from_class(unreal.SkyAtmosphere, location=[0, 0, 0])
                if sky_atmo:
                    sky_atmo.set_folder_path("Environment/Atmosphere")
            if self.config.get("GenerateLights", False):
                sun = unreal.EditorLevelLibrary.spawn_actor_from_class(unreal.DirectionalLight, location=[0, 0, 10000], rotation=unreal.Rotator(-50, -30, 0))
                if sun:
                    sun.set_folder_path("Environment/Lights")
                    self._sun_actor = sun
            if self.config.get("GenerateFog", False):
                fog = unreal.EditorLevelLibrary.spawn_actor_from_class(unreal.ExponentialHeightFog, location=[0, 0, 0])
                if fog:
                    fog.set_folder_path("Environment/Atmosphere")
            unreal.EditorLevelLibrary.save_current_level()

    def assemble_map(self) -> None:
        # Ensure map is loaded (may have been created by ensure_map or another script)
        self.ensure_map()

        # Place statics under {hash}/Statics folder
        self._place_instances_from_config(self.config, "Statics", f"{self.base_hash}/Statics")

        # Place terrain, entities, decorators under {hash}/{type} folders
        for type_name in ("Terrain", "Entities", "Decorators"):
            if type_name in self.extra_configs:
                self._place_instances_from_config(self.extra_configs[type_name], type_name, f"{self.base_hash}/{type_name}")

        # Decal types (gated by GenerateDecals toggle)
        if self.config.get("GenerateDecals", False):
            for type_name in ("RoadDecals", "WaterDecals"):
                if type_name in self.extra_configs:
                    self._place_instances_from_config(self.extra_configs[type_name], type_name, f"{self.base_hash}/{type_name}")
            self.import_decals()

        # SpeedTrees (gated by GenerateSpeedTrees toggle)
        if self.config.get("GenerateSpeedTrees", False) and "SpeedTrees" in self.extra_configs:
            self._place_instances_from_config(self.extra_configs["SpeedTrees"], "SpeedTrees", f"{self.base_hash}/SpeedTrees")

        # Sky objects go into shared Environment folder
        if "SkyObjects" in self.extra_configs:
            self._place_instances_from_config(self.extra_configs["SkyObjects"], "SkyObjects", "Environment/SkyObjects")

        unreal.EditorLevelLibrary.save_current_level()

    def _place_instances_from_config(self, config: dict, type_name: str, folder_path: str = "") -> None:
        """Place mesh instances in the level for a given asset type.

        folder_path: World Outliner folder path for placed actors (e.g. "935DC680/Statics").
        """
        import re

        asset_dir = f'/Game/{self.content_path}/{type_name}/'
        if not unreal.EditorAssetLibrary.does_directory_exist(asset_dir):
            return

        instance_keys = set(config["Instances"].keys())

        # Build map of instance key -> list of UE asset paths
        asset_map = {}
        for x in unreal.EditorAssetLibrary.list_assets(asset_dir, recursive=False):
            # Skip non-mesh assets (PhysicsAsset, Skeleton, etc.)
            asset = unreal.EditorAssetLibrary.load_asset(x)
            if asset is None or not isinstance(asset, (unreal.StaticMesh, unreal.SkeletalMesh)):
                continue
            asset_name = x.split('/')[-1].split('.')[0]
            clean_name = re.sub(r'_ncl\d+_\d+$', '', asset_name)

            # Direct match
            name = clean_name if clean_name in instance_keys else None

            # For terrain/decorators: FBX files are {hash}_{group}.fbx, instance key is {hash}
            # Try stripping the last _N suffix to match the instance key
            if name is None:
                base = re.sub(r'_\d+$', '', clean_name)
                if base in instance_keys:
                    name = base

            # Fallback: try Parts config mapping
            if name is None:
                for mesh_hash, parts_data in config.get("Parts", {}).items():
                    if isinstance(parts_data, dict):
                        part_materials = parts_data.get("PartMaterials", parts_data)
                        for part_name in part_materials.keys():
                            base = re.sub(r'_Group\d+.*$', '', part_name)
                            if base == clean_name and mesh_hash in instance_keys:
                                name = mesh_hash
                                break
                    if name is not None:
                        break

            if name is None:
                name = clean_name

            if name not in asset_map:
                asset_map[name] = []
            asset_map[name].append(x)

        for inst_key, instances in config["Instances"].items():
            if inst_key not in asset_map:
                continue
            parts = asset_map[inst_key]
            for part in parts:
                sm = unreal.EditorAssetLibrary.load_asset(part)
                if sm is None:
                    continue
                for instance in instances:
                    quat = unreal.Quat(instance["Rotation"][0], instance["Rotation"][1], instance["Rotation"][2], instance["Rotation"][3])
                    euler = quat.euler()
                    rotator = unreal.Rotator(-euler.x+180, -euler.y+180, -euler.z)
                    location = [-instance["Translation"][0]*100, instance["Translation"][1]*100, instance["Translation"][2]*100]
                    s = unreal.EditorLevelLibrary.spawn_actor_from_object(sm, location=location, rotation=rotator)
                    if s is None:
                        continue
                    # Scale can be either a scalar (1.3.2) or [x,y,z] array (2.4.7+)
                    scale = instance['Scale']
                    if isinstance(scale, list):
                        s.set_actor_label(s.get_actor_label() + f"_{scale[0]}")
                        s.set_actor_relative_scale3d(scale)
                    else:
                        s.set_actor_label(s.get_actor_label() + f"_{scale}")
                        s.set_actor_relative_scale3d([scale]*3)
                    if folder_path:
                        s.set_folder_path(folder_path)

    def assign_map_materials(self) -> None:
        self._assign_materials_in_dir(self.config, f'/Game/{self.content_path}/Statics/')

    def assign_type_materials(self, type_name: str) -> None:
        """Assign materials to meshes in a type-specific directory using the type's config."""
        cfg = self.extra_configs[type_name]
        asset_dir = f'/Game/{self.content_path}/{type_name}/'
        if unreal.EditorAssetLibrary.does_directory_exist(asset_dir):
            self._assign_materials_in_dir(cfg, asset_dir)

    def _resolve_material(self, mat_hash: str, interop_path: str, cache: dict):
        """load_asset a material by hash, memoised in the supplied cache dict.

        Most meshes reference a small set of materials repeated across many slots,
        and many meshes in a directory share materials with each other — so a single
        cache across an _assign_materials_in_dir pass collapses thousands of editor
        round-trips into one per unique hash.
        """
        if mat_hash in cache:
            return cache[mat_hash]
        asset = unreal.load_asset(f"/Game/{interop_path}/Materials/M_{mat_hash}")
        cache[mat_hash] = asset
        return asset

    def _assign_materials_in_dir(self, config: dict, asset_dir: str) -> None:
        """Assign materials to all meshes in a directory using the given config."""
        import re
        interop_path = config.get('UnrealInteropPath', self.config['UnrealInteropPath'])
        mat_cache = {}
        for x in unreal.EditorAssetLibrary.list_assets(asset_dir, recursive=False):
            mesh = unreal.load_asset(x)
            if mesh is None:
                continue
            # Skip non-mesh assets (e.g. PhysicsAsset, Skeleton)
            is_skeletal = isinstance(mesh, unreal.SkeletalMesh)
            is_static = isinstance(mesh, unreal.StaticMesh)
            if not is_skeletal and not is_static:
                continue
            mat_prop = "materials" if is_skeletal else "static_materials"
            mesh_materials = mesh.get_editor_property(mat_prop)
            new_mesh_materials = []
            for skeletal_material in mesh_materials:
                slot_name = skeletal_material.get_editor_property("material_slot_name").__str__()
                # Strip UE5 duplicate suffix (_ncl1_N)
                mat_hash = re.sub(r'_ncl\d+_\d+$', '', slot_name)
                mat_asset = self._resolve_material(mat_hash, interop_path, mat_cache)
                if mat_asset:
                    skeletal_material.set_editor_property("material_interface", mat_asset)
                new_mesh_materials.append(skeletal_material)
            mesh.set_editor_property(mat_prop, new_mesh_materials)

    def assign_static_materials(self) -> None:
        import re
        mesh = unreal.load_asset(f"/Game/{self.content_path}/{self.config['MeshName']}")
        mesh_materials = mesh.get_editor_property("static_materials")
        new_mesh_materials = []
        mat_cache = {}
        interop_path = self.config['UnrealInteropPath']
        for skeletal_material in mesh_materials:
            slot_name = skeletal_material.get_editor_property("material_slot_name").__str__()
            mat_hash = re.sub(r'_ncl\d+_\d+$', '', slot_name)
            mat_asset = self._resolve_material(mat_hash, interop_path, mat_cache)
            if mat_asset:
                skeletal_material.set_editor_property("material_interface", mat_asset)
            new_mesh_materials.append(skeletal_material)
        mesh.set_editor_property("static_materials", new_mesh_materials)

    def assign_entity_materials(self) -> None:
        import re
        mesh = unreal.load_asset(f"/Game/{self.content_path}/{self.config['MeshName']}")
        mesh_materials = mesh.get_editor_property("materials")
        new_mesh_materials = []
        mat_cache = {}
        interop_path = self.config['UnrealInteropPath']
        for skeletal_material in mesh_materials:
            slot_name = skeletal_material.get_editor_property("material_slot_name").__str__()
            mat_hash = re.sub(r'_ncl\d+_\d+$', '', slot_name)
            mat_asset = self._resolve_material(mat_hash, interop_path, mat_cache)
            if mat_asset:
                skeletal_material.set_editor_property("material_interface", mat_asset)
            new_mesh_materials.append(skeletal_material)
        mesh.set_editor_property("materials", new_mesh_materials)

    def import_entity_mesh(self) -> None:
        task = unreal.AssetImportTask()
        task.set_editor_property("automated", True)
        task.set_editor_property("destination_path", f"/Game/{self.content_path}/")
        task.set_editor_property("filename", f"{self.folder_path}/{self.config['MeshName']}.fbx")
        task.set_editor_property("replace_existing", True)
        # save:False — trailing save_directory in import_entity/static/map persists everything once.
        task.set_editor_property("save", False)

        options = unreal.FbxImportUI()
        options.set_editor_property('import_mesh', True)
        options.set_editor_property('import_textures', False)
        options.set_editor_property('import_materials', False)
        options.set_editor_property('import_as_skeletal', True)
        options.static_mesh_import_data.set_editor_property('convert_scene', False)
        options.static_mesh_import_data.set_editor_property('combine_meshes', False)
        options.static_mesh_import_data.set_editor_property('generate_lightmap_u_vs', False)
        options.static_mesh_import_data.set_editor_property('auto_generate_collision', False)
        options.static_mesh_import_data.set_editor_property("vertex_color_import_option", unreal.VertexColorImportOption.REPLACE)
        options.static_mesh_import_data.set_editor_property("build_nanite", False)
        task.set_editor_property("options", options)

        unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])

    def import_static_mesh(self, combine) -> None:
        task = unreal.AssetImportTask()
        task.set_editor_property("automated", True)
        task.set_editor_property("destination_path", f"/Game/{self.content_path}/Statics/")
        task.set_editor_property("filename", f"{self.folder_path}/{self.config['MeshName']}.fbx")
        task.set_editor_property("replace_existing", True)
        # save:False — trailing save_directory in import_entity/static/map persists everything once.
        task.set_editor_property("save", False)

        options = unreal.FbxImportUI()
        options.set_editor_property('import_mesh', True)
        options.set_editor_property('import_textures', False)
        options.set_editor_property('import_materials', False)
        options.set_editor_property('import_as_skeletal', False)
        options.static_mesh_import_data.set_editor_property('convert_scene', False)
        options.static_mesh_import_data.set_editor_property('import_uniform_scale', 100.0)
        options.static_mesh_import_data.set_editor_property('combine_meshes', combine)
        options.static_mesh_import_data.set_editor_property('generate_lightmap_u_vs', False)
        options.static_mesh_import_data.set_editor_property('auto_generate_collision', False)
        options.static_mesh_import_data.set_editor_property('normal_import_method', unreal.FBXNormalImportMethod.FBXNIM_IMPORT_NORMALS)
        options.static_mesh_import_data.set_editor_property("vertex_color_import_option", unreal.VertexColorImportOption.REPLACE)
        options.static_mesh_import_data.set_editor_property("build_nanite", False)
        task.set_editor_property("options", options)

        unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])

    def _get_material_hashes(self, config=None) -> list:
        """Collect unique material hashes from Parts config."""
        if config is None:
            config = self.config
        hashes = set()
        for parts_data in config.get("Parts", {}).values():
            if isinstance(parts_data, dict):
                for mat_hash in parts_data.get("PartMaterials", {}).values():
                    if mat_hash:
                        hashes.add(mat_hash)
        return list(hashes)

    def _load_material_json(self, mat_hash: str) -> dict:
        """Load a material's JSON file from the Materials/ folder."""
        assets_path = self.config.get("AssetsPath", self.folder_path)
        mat_path = os.path.join(assets_path, "Materials", f"{mat_hash}.json")
        if not os.path.exists(mat_path):
            mat_path = os.path.join(self.folder_path, "Materials", f"{mat_hash}.json")
        if not os.path.exists(mat_path):
            return None
        return json.load(open(mat_path))

    def make_materials(self, config=None) -> None:
        if config is None:
            config = self.config
        interop_path = config.get('UnrealInteropPath', self.config['UnrealInteropPath'])
        materials = self._get_material_hashes(config)

        # Filter to materials that don't already exist; we only do work for those.
        pending = [
            mat for mat in materials
            if not unreal.EditorAssetLibrary.does_asset_exist(f"/Game/{interop_path}/Materials/M_{mat}")
        ]
        if not pending:
            return

        # Phase 1: batch-import every unique texture referenced by any pending material in
        # a single import_asset_tasks call. UE5 amortises per-asset overhead far better when
        # given one large batch than N small ones (the previous behaviour ran one batch per
        # material from inside add_textures).
        self._batch_import_material_textures(pending)

        # Phase 2: build the material graphs. Per-material recompile_material is intentionally
        # dropped — the trailing save_directory in import_entity/static/map triggers shader
        # compilation once per saved asset, which UE5 coalesces. Recompiling N times here was
        # the dominant UE5-side cost in the previous flow.
        for mat in pending:
            try:
                self.make_material(mat, config)
            except Exception:
                pass

    def _batch_import_material_textures(self, material_hashes: list) -> None:
        """Collect texture filenames from every material's JSON, dedupe, and import in one batch."""
        seen = set()
        tasks = []
        dest_path = f'/Game/{self.content_path}/Textures'

        for mat_hash in material_hashes:
            mat_json = self._load_material_json(mat_hash)
            if mat_json is None:
                continue
            ps_textures = mat_json.get("Material", {}).get("Pixel", {}).get("Textures", {})
            for _, texstruct in ps_textures.items():
                tex_path = f"{self.folder_path}/Textures/{texstruct['Hash']}.dds"
                if tex_path in seen:
                    continue
                if not os.path.exists(tex_path):
                    continue
                seen.add(tex_path)
                task = unreal.AssetImportTask()
                task.set_editor_property('filename', tex_path)
                task.set_editor_property('destination_path', dest_path)
                # save:False — the trailing save_directory persists everything once at the end.
                task.set_editor_property('save', False)
                task.set_editor_property('replace_existing', False)
                task.set_editor_property('automated', True)
                tasks.append(task)

        if tasks:
            unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks(tasks)

    @staticmethod
    def _parse_usf_metadata(usf_content: str) -> dict:
        """Parse metadata comments from the top of a .usf file's content.

        Returns dict with keys like 'blend_mode', 'two_sided', 'shading_model',
        plus legacy 'is_transparent' and 'is_masked' booleans for compatibility.
        """
        meta = {
            'blend_mode': 'opaque',
            'two_sided': False,
            'shading_model': 'default_lit',
            'material_domain': 'surface',
            'is_transparent': False,
            'is_masked': False,
        }
        content = usf_content

        # Parse structured metadata comments: // key: value
        for line in content.splitlines():
            line = line.strip()
            if not line.startswith("//"):
                continue
            if ": " not in line:
                # Legacy markers
                if line == "// transparent":
                    meta['is_transparent'] = True
                elif line == "// masked":
                    meta['is_masked'] = True
                continue
            key, _, val = line[2:].strip().partition(": ")
            key = key.strip()
            val = val.strip()
            if key == "blend_mode":
                meta['blend_mode'] = val
            elif key == "two_sided":
                meta['two_sided'] = val.lower() == "true"
            elif key == "shading_model":
                meta['shading_model'] = val
            elif key == "material_domain":
                meta['material_domain'] = val

        # Sync legacy flags from structured metadata
        if meta['blend_mode'] in ('translucent', 'additive', 'modulate'):
            meta['is_transparent'] = True
        elif meta['blend_mode'] == 'masked':
            meta['is_masked'] = True

        return meta

    @staticmethod
    def _apply_material_metadata(material: unreal.Material, meta: dict) -> None:
        """Apply blend mode, shading model, and two-sided settings from parsed metadata."""
        blend_map = {
            'opaque': unreal.BlendMode.BLEND_OPAQUE,
            'translucent': unreal.BlendMode.BLEND_TRANSLUCENT,
            'additive': unreal.BlendMode.BLEND_ADDITIVE,
            'modulate': unreal.BlendMode.BLEND_MODULATE,
            'masked': unreal.BlendMode.BLEND_MASKED,
        }
        blend_mode = blend_map.get(meta['blend_mode'], unreal.BlendMode.BLEND_OPAQUE)
        material.set_editor_property("blend_mode", blend_mode)

        material.set_editor_property("two_sided", meta['two_sided'])

        # Material domain
        if meta.get('material_domain') == 'deferred_decal':
            material.set_editor_property("material_domain", unreal.MaterialDomain.MD_DEFERRED_DECAL)
            material.set_editor_property("decal_blend_mode", unreal.DecalBlendMode.DBM_TRANSLUCENT)

        # Shading model
        if meta['shading_model'] == 'unlit':
            material.set_editor_property("shading_model", unreal.MaterialShadingModel.MSM_UNLIT)

    def make_material(self, matstr: str, config=None) -> unreal.Material:
        if config is None:
            config = self.config
        interop_path = config.get('UnrealInteropPath', self.config['UnrealInteropPath'])

        mat_json = self._load_material_json(matstr)
        if mat_json is None:
            return None

        # Make base material
        material = unreal.AssetToolsHelpers.get_asset_tools().create_asset("M_" + matstr, f"/Game/{interop_path}/Materials", unreal.Material, unreal.MaterialFactoryNew())

        usf_path = f"{self.folder_path}/Shaders/Unreal/PS_{matstr}.usf"
        if os.path.exists(usf_path):
            # Read USF once; downstream uses both the metadata and the raw code.
            with open(usf_path, "r") as f:
                usf_content = f.read()

            # Parse metadata from .usf header
            meta = self._parse_usf_metadata(usf_content)

            # Apply blend mode, two-sided, shading model from metadata
            self._apply_material_metadata(material, meta)

            # Add textures
            texture_samples = self.add_textures(material, matstr, mat_json)

            # Add custom node
            custom_node = self.add_custom_node(material, texture_samples, matstr, mat_json, meta, usf_content)

            # Set output, not using in-built custom expression system because I want to leave it open for manual control
            self.create_output(material, custom_node, meta['is_transparent'])
        else:
            material.set_editor_property("blend_mode", unreal.BlendMode.BLEND_MASKED)
            const = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionConstant, -300, 0)
            unreal.MaterialEditingLibrary.connect_material_property(const, "", unreal.MaterialProperty.MP_OPACITY_MASK)

        return material

    def create_output(self, material: unreal.Material, custom_node: unreal.MaterialExpressionCustom, is_transparent: bool = False) -> None:
        mat_att = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionBreakMaterialAttributes, -300, 0)
        # Connect custom node to the new break
        unreal.MaterialEditingLibrary.connect_material_expressions(custom_node, '', mat_att, 'Attr')
        # Connect all outputs
        unreal.MaterialEditingLibrary.connect_material_property(mat_att, "BaseColor", unreal.MaterialProperty.MP_BASE_COLOR)
        unreal.MaterialEditingLibrary.connect_material_property(mat_att, "Metallic", unreal.MaterialProperty.MP_METALLIC)
        unreal.MaterialEditingLibrary.connect_material_property(mat_att, "Roughness", unreal.MaterialProperty.MP_ROUGHNESS)
        unreal.MaterialEditingLibrary.connect_material_property(mat_att, "EmissiveColor", unreal.MaterialProperty.MP_EMISSIVE_COLOR)
        if is_transparent:
            unreal.MaterialEditingLibrary.connect_material_property(mat_att, "Opacity", unreal.MaterialProperty.MP_OPACITY)
        else:
            unreal.MaterialEditingLibrary.connect_material_property(mat_att, "OpacityMask", unreal.MaterialProperty.MP_OPACITY_MASK)
        unreal.MaterialEditingLibrary.connect_material_property(mat_att, "Normal", unreal.MaterialProperty.MP_NORMAL)
        unreal.MaterialEditingLibrary.connect_material_property(mat_att, "AmbientOcclusion", unreal.MaterialProperty.MP_AMBIENT_OCCLUSION)

    def add_custom_node(self, material: unreal.Material, texture_nodes: list, matstr: str, mat_json: dict, meta: dict = None, usf_content: str = None) -> unreal.MaterialExpressionCustom:
        import re as _re

        ps_textures = mat_json.get("Material", {}).get("Pixel", {}).get("Textures", {})
        all_cfg_indices = sorted([int(x) for x in ps_textures.keys()])
        # Filter to 2D-only material texture indices (matches V2 ClassifyTextures)
        all_2d_indices = [int(x) for x, t in ps_textures.items() if t.get('Dimension', '2D') == '2D']
        all_2d_indices.sort()

        custom_node = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionCustom, -500, 0)

        # Prefer caller-supplied content (avoids a second disk read in make_material);
        # fall back to disk for callers that haven't been updated.
        if usf_content is not None:
            code = usf_content
        else:
            with open(f"{self.folder_path}/Shaders/Unreal/PS_{matstr}.usf", "r") as f:
                code = f.read()

        # Use metadata if provided, otherwise fall back to legacy comment detection
        if meta is None:
            is_transparent = "// transparent" in code
        else:
            is_transparent = meta.get('is_transparent', False)

        # V2: scan for Material_Texture2D_N references to find how many texture inputs are needed
        used_positions = sorted(set(int(m.group(1)) for m in _re.finditer(r"Material_Texture2D_(\d+)(?:\.|Sampler)", code)))
        n_texture_inputs = (max(used_positions) + 1) if used_positions else 0
        tex_input_names = [f't{i}' for i in range(n_texture_inputs)]
        # V1 DISABLED — all shaders now go through V2
        # if is_v2:
        #     tex_input_names = sorted(set(_re.findall(r'Texture2DSampleLevel\((t\d+),', code)))
        #     n_texture_inputs = len(tex_input_names)
        # else:
        #     used_positions = sorted(set(int(m.group(1)) for m in _re.finditer(r"Material_Texture2D_(\d+)(?:\.|Sampler)", code)))
        #     n_texture_inputs = (max(used_positions) + 1) if used_positions else 0
        #     tex_input_names = [f't{i}' for i in range(n_texture_inputs)]

        inputs = []
        for name in tex_input_names:
            ci = unreal.CustomInput()
            ci.set_editor_property('input_name', name)
            inputs.append(ci)
        if is_transparent:
            sp = unreal.CustomInput()
            sp.set_editor_property('input_name', 'screenPos')
            inputs.append(sp)
            tss = unreal.CustomInput()
            tss.set_editor_property('input_name', 'twoSidedSign')
            inputs.append(tss)
        ci = unreal.CustomInput()
        ci.set_editor_property('input_name', 'tx')
        inputs.append(ci)
        vc = unreal.CustomInput()
        vc.set_editor_property('input_name', 'vc')
        inputs.append(vc)
        vcw = unreal.CustomInput()
        vcw.set_editor_property('input_name', 'vcw')
        inputs.append(vcw)
        viewdir = unreal.CustomInput()
        viewdir.set_editor_property('input_name', 'viewDir')
        inputs.append(viewdir)

        custom_node.set_editor_property('code', code)
        custom_node.set_editor_property('inputs', inputs)
        custom_node.set_editor_property('output_type', unreal.CustomMaterialOutputType.CMOT_MATERIAL_ATTRIBUTES)

        # Connect TextureSampleParameter2D nodes via RGBA output to register them as
        # Material_Texture2D_N in UE5's material system (sequential by input order).
        # The custom expression code references Material_Texture2D_N directly for sampling.
        default_tex = unreal.load_asset('/Engine/EngineMaterials/DefaultDiffuse')
        for seq in range(n_texture_inputs):
            input_name = f't{seq}'
            if seq < len(all_2d_indices):
                orig_idx = all_2d_indices[seq]
                if orig_idx in texture_nodes:
                    unreal.MaterialEditingLibrary.connect_material_expressions(
                        texture_nodes[orig_idx], 'RGBA', custom_node, input_name)
                    continue
            # Placeholder for missing textures so Material_Texture2D_N indices stay sequential
            placeholder = unreal.MaterialEditingLibrary.create_material_expression(
                material, unreal.MaterialExpressionTextureSampleParameter2D, -1000, -500 + 250 * seq)
            if default_tex:
                placeholder.set_editor_property('texture', default_tex)
            unreal.MaterialEditingLibrary.connect_material_expressions(
                placeholder, 'RGBA', custom_node, input_name)

        texcoord = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionTextureCoordinate, -500, 400)
        unreal.MaterialEditingLibrary.connect_material_expressions(texcoord, '', custom_node, 'tx')

        vertex_color = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionVertexColor, -500, 500)
        unreal.MaterialEditingLibrary.connect_material_expressions(vertex_color, '', custom_node, 'vc')
        unreal.MaterialEditingLibrary.connect_material_expressions(vertex_color, 'A', custom_node, 'vcw')

        cam_vec = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionCameraVectorWS, -500, 700)
        vec_transform = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionTransform, -500, 800)
        vec_transform.set_editor_property('transform_source_type', unreal.MaterialVectorCoordTransformSource.TRANSFORMSOURCE_WORLD)
        vec_transform.set_editor_property('transform_type', unreal.MaterialVectorCoordTransform.TRANSFORM_TANGENT)
        unreal.MaterialEditingLibrary.connect_material_expressions(cam_vec, '', vec_transform, '')
        unreal.MaterialEditingLibrary.connect_material_expressions(vec_transform, '', custom_node, 'viewDir')

        if is_transparent:
            screen_pos = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionScreenPosition, -500, 900)
            unreal.MaterialEditingLibrary.connect_material_expressions(screen_pos, '', custom_node, 'screenPos')

            two_sided = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionTwoSidedSign, -500, 1000)
            unreal.MaterialEditingLibrary.connect_material_expressions(two_sided, '', custom_node, 'twoSidedSign')

        return custom_node

    def add_textures(self, material: unreal.Material, matstr: str, mat_json: dict) -> dict:
        """Wire TextureSampleParameter2D nodes for this material's 2D textures.

        Texture .dds files are imported in a single batch by `_batch_import_material_textures`
        before this runs (called from make_materials), so this method does node wiring only.
        Callers outside the batch path can still rely on `replace_existing=False` semantics —
        re-importing here would no-op for assets that already exist.
        """
        texture_nodes = {}

        ps_textures = mat_json.get("Material", {}).get("Pixel", {}).get("Textures", {})
        srgbs = {int(i): texstruct.get('Colorspace', '') in ('sRGB', 'Srgb') for i, texstruct in ps_textures.items()}

        # V2: create TextureSampleParameter2D nodes for all 2D material textures
        # Connecting these to the custom expression registers them as Material_Texture2D_N
        # in UE5, which always generates companion Material_Texture2D_NSampler declarations.
        for i, texstruct in ps_textures.items():
            i = int(i)
            dim = texstruct.get('Dimension', '2D')

            # Only 2D textures are supported as custom expression inputs for now
            if dim != '2D':
                continue

            tex_node = unreal.MaterialEditingLibrary.create_material_expression(
                material, unreal.MaterialExpressionTextureSampleParameter2D, -1000, -500 + 250 * i)
            tex_node.set_editor_property('parameter_name', f'tex_{texstruct["Hash"]}')

            ts_TextureUePath = f"/Game/{self.content_path}/Textures/{texstruct['Hash']}.{texstruct['Hash']}"
            ts_LoadedTexture = unreal.EditorAssetLibrary.load_asset(ts_TextureUePath)
            if not ts_LoadedTexture:
                continue
            ts_LoadedTexture.set_editor_property('srgb', srgbs.get(i, False))
            if srgbs.get(i, False):
                ts_LoadedTexture.set_editor_property('compression_settings', unreal.TextureCompressionSettings.TC_DEFAULT)
            else:
                ts_LoadedTexture.set_editor_property('compression_settings', unreal.TextureCompressionSettings.TC_VECTOR_DISPLACEMENTMAP)

            tex_node.set_editor_property('texture', ts_LoadedTexture)

            # Sampler type must match texture compression to avoid UE5 type mismatch errors
            actual_compression = ts_LoadedTexture.get_editor_property('compression_settings')
            actual_srgb = ts_LoadedTexture.get_editor_property('srgb')
            if actual_compression == unreal.TextureCompressionSettings.TC_NORMALMAP:
                tex_node.set_editor_property("sampler_type", unreal.MaterialSamplerType.SAMPLERTYPE_NORMAL)
            elif actual_srgb:
                tex_node.set_editor_property("sampler_type", unreal.MaterialSamplerType.SAMPLERTYPE_COLOR)
            else:
                tex_node.set_editor_property("sampler_type", unreal.MaterialSamplerType.SAMPLERTYPE_LINEAR_COLOR)

            texture_nodes[i] = tex_node
            # Per-texture save dropped — the trailing save_directory in import_entity/static/map
            # persists everything in one pass.

        return texture_nodes

    def import_lights(self) -> None:
        """Spawn light actors from exported light data."""
        lights_path = os.path.join(self.folder_path, "Rendering", "Lights.json")
        if not os.path.exists(lights_path):
            return

        with open(lights_path) as f:
            lights = json.load(f)

        light_type_map = {
            "Point": unreal.PointLight,
            "Spot": unreal.SpotLight,
            "Area": unreal.RectLight,
        }

        count = 0
        for light_key, light_data in lights.items():
            light_class = light_type_map.get(light_data.get("Type"), unreal.PointLight)
            color = light_data.get("Color", [1, 1, 1, 1])
            attenuation = light_data.get("Attenuation", 10.0)

            for i, inst in enumerate(light_data.get("Instances", [])):
                loc = inst["Translation"]
                location = [-loc[0] * 100, loc[1] * 100, loc[2] * 100]
                rot = inst.get("Rotation", [0, 0, 0, 1])
                quat = unreal.Quat(rot[0], rot[1], rot[2], rot[3])
                euler = quat.euler()
                rotator = unreal.Rotator(-euler.x + 180, -euler.y + 180, -euler.z)

                actor = unreal.EditorLevelLibrary.spawn_actor_from_class(light_class, location=location, rotation=rotator)
                if actor is None:
                    continue
                actor.set_actor_label(f"D2_{light_data.get('Type', 'Light')}_{light_key}_{i}")
                actor.set_folder_path("Environment/Lights")

                component = actor.light_component
                component.set_editor_property('light_color', unreal.LinearColor(color[0], color[1], color[2], color[3]))
                component.set_editor_property('attenuation_radius', attenuation * 100)

                scale = inst.get("Scale", [1, 1, 1])
                actor.set_actor_relative_scale3d(scale)
                count += 1

    def import_cubemaps(self) -> None:
        """Spawn reflection captures from exported cubemap data."""
        cubemap_path = os.path.join(self.folder_path, "Rendering", "Cubemaps.json")
        if not os.path.exists(cubemap_path):
            return

        with open(cubemap_path) as f:
            cubemaps = json.load(f)

        # Import cubemap textures
        tex_folder = os.path.join(self.folder_path, "Textures", "Cubemaps")
        if os.path.exists(tex_folder):
            import glob
            tex_files = glob.glob(os.path.join(tex_folder, "*.dds"))
            if tex_files:
                tasks = []
                for tex_file in tex_files:
                    task = unreal.AssetImportTask()
                    task.set_editor_property('filename', tex_file)
                    task.set_editor_property('destination_path', f'/Game/{self.content_path}/Textures/Cubemaps')
                    task.set_editor_property('save', False)
                    task.set_editor_property('replace_existing', False)
                    task.set_editor_property('automated', True)
                    tasks.append(task)
                unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks(tasks)

        count = 0
        for name, data in cubemaps.items():
            transform = data.get("Transform", {})
            shape = data.get("CubemapShape", "Sphere")
            loc = transform.get("Translation", [0, 0, 0])
            location = [-loc[0] * 100, loc[1] * 100, loc[2] * 100]

            if shape == "Box":
                actor = unreal.EditorLevelLibrary.spawn_actor_from_class(unreal.BoxReflectionCapture, location=location)
            else:
                actor = unreal.EditorLevelLibrary.spawn_actor_from_class(unreal.SphereReflectionCapture, location=location)

            if actor is None:
                continue

            actor.set_actor_label(f"D2_Cubemap_{name}")
            actor.set_folder_path("Environment/Cubemaps")

            rot = transform.get("Rotation", [0, 0, 0, 1])
            quat = unreal.Quat(rot[0], rot[1], rot[2], rot[3])
            euler = quat.euler()
            actor.set_actor_rotation(unreal.Rotator(-euler.x + 180, -euler.y + 180, -euler.z), False)

            scale = transform.get("Scale", [1, 1, 1])
            actor.set_actor_relative_scale3d(scale)
            count += 1

    def import_atmosphere(self) -> None:
        """Import atmosphere LUT textures and set sun direction from day cycle data."""
        atmo_path = os.path.join(self.folder_path, "Rendering", "Atmosphere.json")
        if not os.path.exists(atmo_path):
            return

        with open(atmo_path) as f:
            atmo = json.load(f)

        # Import atmosphere LUT textures as reference
        tex_folder = os.path.join(self.folder_path, "Textures", "Atmosphere")
        if os.path.exists(tex_folder):
            import glob
            tex_files = glob.glob(os.path.join(tex_folder, "*.dds"))
            if tex_files:
                tasks = []
                for tex_file in tex_files:
                    task = unreal.AssetImportTask()
                    task.set_editor_property('filename', tex_file)
                    task.set_editor_property('destination_path', f'/Game/{self.content_path}/Textures/Atmosphere')
                    task.set_editor_property('save', False)
                    task.set_editor_property('replace_existing', False)
                    task.set_editor_property('automated', True)
                    tasks.append(task)
                unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks(tasks)

        # Set sun direction from day cycle first rotation keyframe
        day_cycle = atmo.get("DayCycle")
        if day_cycle and day_cycle.get("DayCycleRotations"):
            rotations = day_cycle["DayCycleRotations"]
            first_rot = None
            for rot_data in rotations:
                rots = rot_data.get("Rotations", [])
                if rots:
                    first_rot = rots[0]
                    break

            if first_rot:
                # Vector4 serializes as {"X":..., "Y":..., "Z":..., "W":...} from C#
                if isinstance(first_rot, dict):
                    qx, qy, qz, qw = first_rot.get("X", 0), first_rot.get("Y", 0), first_rot.get("Z", 0), first_rot.get("W", 1)
                else:
                    qx, qy, qz, qw = first_rot[0], first_rot[1], first_rot[2], first_rot[3]

                quat = unreal.Quat(qx, qy, qz, qw)
                euler = quat.euler()
                sun_rotator = unreal.Rotator(-euler.x + 180, -euler.y + 180, -euler.z)

                # Prefer the stashed reference from ensure_map; only walk all level actors
                # if we don't have one (e.g. importing into a pre-existing level).
                sun_actor = self._sun_actor
                if sun_actor is None:
                    for actor in unreal.EditorLevelLibrary.get_all_level_actors():
                        if isinstance(actor, unreal.DirectionalLight):
                            sun_actor = actor
                            self._sun_actor = actor
                            break

                if sun_actor is not None:
                    sun_actor.set_actor_rotation(sun_rotator, False)
                    sun_actor.set_actor_label("D2_Sun")
                    sun_actor.set_folder_path("Environment/Lights")

    def _make_decal_material(self, decal_key: str) -> unreal.Material:
        """Create a Deferred Decal material for a decal hash using its first texture."""
        interop_path = self.config.get('UnrealInteropPath', self.config['UnrealInteropPath'])
        mat_path = f"/Game/{interop_path}/Materials/MD_{decal_key}"
        if unreal.EditorAssetLibrary.does_asset_exist(mat_path):
            return unreal.load_asset(mat_path)

        mat_json = self._load_material_json(decal_key)
        if mat_json is None:
            return None

        ps_textures = mat_json.get("Material", {}).get("Pixel", {}).get("Textures", {})
        if not ps_textures:
            return None

        # Pick the first sRGB texture as diffuse, fallback to first available
        diffuse_tex = None
        for idx, texstruct in sorted(ps_textures.items(), key=lambda x: int(x[0])):
            if texstruct.get("Dimension", "2D") != "2D":
                continue
            if diffuse_tex is None:
                diffuse_tex = texstruct
            if texstruct.get("Colorspace", "") in ("sRGB", "Srgb"):
                diffuse_tex = texstruct
                break

        if diffuse_tex is None:
            return None

        # Import the texture
        tex_file = os.path.join(self.folder_path, "Textures", f"{diffuse_tex['Hash']}.dds")
        if os.path.exists(tex_file):
            task = unreal.AssetImportTask()
            task.set_editor_property('filename', tex_file)
            task.set_editor_property('destination_path', f'/Game/{self.content_path}/Textures')
            task.set_editor_property('save', False)
            task.set_editor_property('replace_existing', False)
            task.set_editor_property('automated', True)
            unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])

        # Create deferred decal material
        material = unreal.AssetToolsHelpers.get_asset_tools().create_asset(
            f"MD_{decal_key}", f"/Game/{interop_path}/Materials",
            unreal.Material, unreal.MaterialFactoryNew())

        material.set_editor_property("material_domain", unreal.MaterialDomain.MD_DEFERRED_DECAL)
        material.set_editor_property("blend_mode", unreal.BlendMode.BLEND_TRANSLUCENT)
        material.set_editor_property("decal_blend_mode", unreal.DecalBlendMode.DBM_TRANSLUCENT)

        # Add texture sample node
        tex_node = unreal.MaterialEditingLibrary.create_material_expression(
            material, unreal.MaterialExpressionTextureSample, -300, 0)
        tex_ue_path = f"/Game/{self.content_path}/Textures/{diffuse_tex['Hash']}.{diffuse_tex['Hash']}"
        loaded_tex = unreal.EditorAssetLibrary.load_asset(tex_ue_path)
        if loaded_tex:
            is_srgb = diffuse_tex.get("Colorspace", "") in ("sRGB", "Srgb")
            loaded_tex.set_editor_property('srgb', is_srgb)
            tex_node.set_editor_property('texture', loaded_tex)

        # Wire base color and alpha
        unreal.MaterialEditingLibrary.connect_material_property(tex_node, "RGB", unreal.MaterialProperty.MP_BASE_COLOR)
        unreal.MaterialEditingLibrary.connect_material_property(tex_node, "A", unreal.MaterialProperty.MP_OPACITY)

        unreal.MaterialEditingLibrary.recompile_material(material)
        return material

    def import_decals(self) -> None:
        """Spawn decal actors from exported decal data into {hash}/Decals folder."""
        decals_path = os.path.join(self.folder_path, "Rendering", "Decals.json")
        if not os.path.exists(decals_path):
            return

        with open(decals_path) as f:
            decals = json.load(f)

        interop_path = self.config.get('UnrealInteropPath', self.config['UnrealInteropPath'])

        # add_textures relies on textures being pre-imported by _batch_import_material_textures
        # (normally done from make_materials). Decals call make_material directly and bypass
        # that path, so batch-import their textures here for the decal_keys we'll actually build.
        new_decal_keys = [
            d for d in decals
            if not unreal.EditorAssetLibrary.does_asset_exist(f"/Game/{interop_path}/Materials/M_{d}")
        ]
        if new_decal_keys:
            self._batch_import_material_textures(new_decal_keys)

        # Create decal materials using the full shader pipeline
        decal_materials = {}
        for decal_key in decals:
            try:
                mat_path = f"/Game/{interop_path}/Materials/M_{decal_key}"
                if unreal.EditorAssetLibrary.does_asset_exist(mat_path):
                    decal_materials[decal_key] = unreal.load_asset(mat_path)
                else:
                    mat = self.make_material(decal_key)
                    if mat is not None:
                        # Ensure decal domain even if USF metadata is missing (pre-rebuild exports)
                        mat.set_editor_property("material_domain", unreal.MaterialDomain.MD_DEFERRED_DECAL)
                        mat.set_editor_property("decal_blend_mode", unreal.DecalBlendMode.DBM_TRANSLUCENT)
                        # Decal domain change after creation — recompile is load-bearing here,
                        # without it the decal renders with the wrong domain on first paint.
                        unreal.MaterialEditingLibrary.recompile_material(mat)
                        decal_materials[decal_key] = mat
            except Exception:
                pass

        count = 0
        for decal_key, decal_data in decals.items():
            mat_asset = decal_materials.get(decal_key)

            for i, inst in enumerate(decal_data.get("Instances", [])):
                loc = inst["Translation"]
                location = [-loc[0] * 100, loc[1] * 100, loc[2] * 100]
                rot = inst.get("Rotation", [0, 0, 0, 1])
                quat = unreal.Quat(rot[0], rot[1], rot[2], rot[3])
                euler = quat.euler()
                rotator = unreal.Rotator(-euler.x + 180, -euler.y + 180, -euler.z)

                actor = unreal.EditorLevelLibrary.spawn_actor_from_class(unreal.DecalActor, location=location, rotation=rotator)
                if actor is None:
                    continue

                actor.set_actor_label(f"D2_Decal_{decal_key}_{i}")
                actor.set_folder_path(f"{self.base_hash}/Decals")

                # Set decal size from scale (UE5 DecalComponent uses size in cm, not actor scale)
                scale = inst.get("Scale", [1, 1, 1])
                actor.decal.set_editor_property('decal_size', unreal.Vector(scale[0] * 100, scale[1] * 100, scale[2] * 100))

                if mat_asset:
                    actor.set_decal_material(mat_asset)

                count += 1

    def import_lens_flares(self) -> None:
        """Spawn lens flare placeholder actors from exported data into Environment/LensFlares folder."""
        flares_path = os.path.join(self.folder_path, "Rendering", "LensFlares.json")
        if not os.path.exists(flares_path):
            return

        with open(flares_path) as f:
            flares = json.load(f)

        count = 0
        for flare_key, flare_data in flares.items():
            for i, inst in enumerate(flare_data.get("Instances", [])):
                loc = inst["Translation"]
                location = [-loc[0] * 100, loc[1] * 100, loc[2] * 100]
                rot = inst.get("Rotation", [0, 0, 0, 1])
                quat = unreal.Quat(rot[0], rot[1], rot[2], rot[3])
                euler = quat.euler()
                rotator = unreal.Rotator(-euler.x + 180, -euler.y + 180, -euler.z)

                # Spawn as a point light with low intensity as a visible placeholder
                actor = unreal.EditorLevelLibrary.spawn_actor_from_class(unreal.PointLight, location=location, rotation=rotator)
                if actor is None:
                    continue

                actor.set_actor_label(f"D2_LensFlare_{flare_key}_{i}")
                actor.set_folder_path("Environment/LensFlares")

                # Keep intensity minimal — these are positional markers for manual lens flare setup
                actor.light_component.set_editor_property('intensity', 0.1)
                actor.light_component.set_editor_property('attenuation_radius', 50.0)

                scale = inst.get("Scale", [1, 1, 1])
                actor.set_actor_relative_scale3d(scale)
                count += 1

    """
    Updates all materials used by this model to the latest .usfs found in the Shaders/ folder.
    Very useful for improving the material quality without much manual work.
    """
    def update_material_code(self) -> None:
        # Get all materials to update
        materials = self._get_material_hashes()

        # For each material, find the code node and update it
        mats = {unreal.EditorAssetLibrary.load_asset(f"/Game/{self.config['UnrealInteropPath']}/Materials/M_{matstr}"): matstr for matstr in materials}
        it = unreal.ObjectIterator()
        for x in it:
            if x.get_outer() in mats:
                if isinstance(x, unreal.MaterialExpressionCustom):
                    code = open(f"{self.folder_path}/Shaders/Unreal/PS_{mats[x.get_outer()]}.usf", "r").read()
                    x.set_editor_property('code', code)

        unreal.EditorAssetLibrary.save_directory(f"/Game/{self.content_path}/Materials/", False)


if __name__ == "__main__":
    importer = CharmImporter(os.path.dirname(os.path.realpath(__file__)), b_unique_folder=False)
    importer.import_entity()
    # importer.update_material_code()
