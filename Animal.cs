using SharpPluginLoader.Core.Memory;

namespace SharpPluginLoader.Core.Entities
{
    public class Animal : Entity
    {
        /// <summary>
        /// The sAnimal singleton instance
        /// </summary>
        public static MtObject SingletonInstance => SingletonManager.GetSingleton("sAnimal")!;

        /// <summary>
        /// Gets a list of all animals in the game
        /// </summary>
        /// <returns></returns>
        public static Animal[] GetAllAnimals()
        {
            lock (_animals)
                return _animals.ToArray();
        }

        /// <summary>
        /// Constructs a new Animal from a native pointer
        /// </summary>
        /// <param name="instance">The native pointer</param>
        public Animal(nint instance) : base(instance) { }

        // @TODO: Map to names.
        public int Id => Get<int>(0x1AF8); // +0x294
        public int OtherId => Get<int>(0x1AF0);

        private static void AnimalAddHook(nint instance)
        {
            var animal = new Animal(instance);
            lock (_animals) _animals.Add(animal);
            _animalAddHook.Original(instance);
        }

        private static nint AnimalRemoveHook(nint instance)
        {
            var animal = new Animal(instance);
            lock(_animals) _animals.Remove(animal);
            return _animalRemoveHook.Original(instance);
        }

        internal static void Initialize()
        {
            _animalAddHook = Hook.Create<AnimalAddDelegate>(0x141D2F610, AnimalAddHook);
            _animalRemoveHook = Hook.Create<AnimalRemoveDelegate>(0x141D289D0, AnimalRemoveHook);
        }

        private delegate void AnimalAddDelegate(nint instance);
        private delegate nint AnimalRemoveDelegate(nint instance);
        private static Hook<AnimalAddDelegate> _animalAddHook = null!;
        private static Hook<AnimalRemoveDelegate> _animalRemoveHook = null!;
        private static readonly List<Animal> _animals = [];
    }
}
